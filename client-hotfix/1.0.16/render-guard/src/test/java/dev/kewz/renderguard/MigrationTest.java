package dev.kewz.renderguard;

import com.google.gson.JsonParser;
import fudge.notenoughcrashes.config.NecMidnightConfig;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/** Filesystem/JSON/reflection fixture tests; no Minecraft classes are loaded. */
public final class MigrationTest {
    private static int passed;
    private static void check(boolean condition, String name) {
        if (!condition) throw new AssertionError(name);
        passed++;
    }
    private static Path file(Path dir) { return dir.resolve("notenoughcrashes.json"); }
    private static Path marker(Path dir) { return dir.resolve(NecRecoveryMigration.STATE_DIR).resolve(NecRecoveryMigration.MARKER); }
    private static Path backup(Path dir) { return dir.resolve(NecRecoveryMigration.STATE_DIR).resolve(NecRecoveryMigration.BACKUP); }

    public static void main(String[] args) throws Exception {
        Path root = Files.createTempDirectory(Path.of(args[0]), "migration-fixture-");
        List<String> warnings = new ArrayList<>();
        Path valid = Files.createDirectory(root.resolve("valid"));
        String original = "{\"catchGameloop\":true,\"crashLimit\":20,\"catchInitializationCrashes\":true,\"unknown\":{\"array\":[1,\"text\",false,null]}}";
        Files.writeString(file(valid), original);
        NecMidnightConfig.catchGameloop = true;
        NecRecoveryMigration.apply(valid, true, RenderGuardClient::synchronizeNec, warnings::add);
        var result = JsonParser.parseString(Files.readString(file(valid))).getAsJsonObject();
        var expected = JsonParser.parseString(original).getAsJsonObject();
        expected.addProperty("catchGameloop", false);
        check(result.equals(expected), "Only intended JSON value changed; unknown nested keys retained");
        check(Files.readString(backup(valid)).equals(original), "Backup preserves exact original bytes");
        check(Files.isRegularFile(marker(valid)), "Completion marker written");
        check(!NecMidnightConfig.catchGameloop, "Actual reflection adapter updates exact static boolean fixture");
        check(warnings.isEmpty(), "Successful migration has no warnings");

        Files.writeString(file(valid), original); // A later deliberate edit to true.
        NecMidnightConfig.catchGameloop = true;
        NecRecoveryMigration.apply(valid, true, RenderGuardClient::synchronizeNec, warnings::add);
        check(Files.readString(file(valid)).equals(original), "Marker preserves later deliberate disk edit");
        check(NecMidnightConfig.catchGameloop, "Marker preserves later deliberate in-memory setting");
        check(Files.readString(backup(valid)).equals(original), "Later launch never replaces original backup");

        Path absent = Files.createDirectory(root.resolve("absent"));
        Files.writeString(file(absent), original);
        NecRecoveryMigration.apply(absent, false, () -> { throw new AssertionError("NEC absent callback"); }, warnings::add);
        check(!Files.exists(absent.resolve(NecRecoveryMigration.STATE_DIR)), "Absent NEC creates no state");
        check(Files.readString(file(absent)).equals(original), "Absent NEC does not edit config");

        Path missing = Files.createDirectory(root.resolve("missing"));
        NecRecoveryMigration.apply(missing, true, () -> { throw new AssertionError("Missing config callback"); }, warnings::add);
        check(!Files.exists(file(missing)) && !Files.exists(marker(missing)), "Missing config not manufactured");

        String[] invalid = {"{", "{\"catchGameloop\":true,}", "{\"catchGameloop\":true} garbage",
                "{\"catchGameloop\":true /*comment*/}", "{catchGameloop:true}", "[]", "{\"catchGameloop\":\"true\"}", "{\"other\":1}"};
        for (int i = 0; i < invalid.length; i++) {
            Path bad = Files.createDirectory(root.resolve("invalid-" + i));
            Files.writeString(file(bad), invalid[i]);
            int before = warnings.size();
            NecRecoveryMigration.apply(bad, true, () -> { throw new AssertionError("Invalid config callback"); }, warnings::add);
            check(Files.readString(file(bad)).equals(invalid[i]), "Invalid JSON remains byte-exact " + i);
            check(!Files.exists(marker(bad)) && !Files.exists(backup(bad)), "Invalid JSON not marked/backed as migrated " + i);
            check(warnings.size() > before, "Invalid JSON warning " + i);
        }

        Path safe = Files.createDirectory(root.resolve("already-false"));
        String safeText = "{ \"catchGameloop\" : false, \"crashLimit\": 20 }\n";
        Files.writeString(file(safe), safeText);
        NecMidnightConfig.catchGameloop = true;
        NecRecoveryMigration.apply(safe, true, RenderGuardClient::synchronizeNec, warnings::add);
        check(Files.readString(file(safe)).equals(safeText), "Already-false JSON is not reformatted");
        check(!NecMidnightConfig.catchGameloop && Files.exists(marker(safe)), "Already-false synchronizes and marks once");

        Path badUtf8 = Files.createDirectory(root.resolve("invalid-utf8"));
        byte[] invalidBytes = {(byte) 0xc3, (byte) 0x28};
        Files.write(file(badUtf8), invalidBytes);
        NecRecoveryMigration.apply(badUtf8, true, () -> { throw new AssertionError("Invalid UTF8 callback"); }, warnings::add);
        check(Arrays.equals(Files.readAllBytes(file(badUtf8)), invalidBytes) && !Files.exists(marker(badUtf8)), "Invalid UTF8 is not silently repaired");

        Path interrupted = Files.createDirectory(root.resolve("interrupted"));
        Files.writeString(file(interrupted), original);
        Files.createDirectory(interrupted.resolve(NecRecoveryMigration.STATE_DIR));
        Files.writeString(backup(interrupted), "original backup");
        NecRecoveryMigration.apply(interrupted, true, () -> { throw new AssertionError("Incomplete migration callback"); }, warnings::add);
        check(Files.readString(file(interrupted)).equals(original) && !Files.exists(marker(interrupted)), "Incomplete prior attempt fails closed");
        check(Files.readString(backup(interrupted)).equals("original backup"), "Incomplete original backup never overwritten");
        System.out.println("PASS " + passed + " assertions; fixture root=" + root);
    }
}
