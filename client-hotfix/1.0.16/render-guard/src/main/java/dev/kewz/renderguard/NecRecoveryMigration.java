package dev.kewz.renderguard;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.stream.JsonReader;
import com.google.gson.stream.JsonToken;
import java.io.StringReader;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.HexFormat;
import java.util.function.Consumer;

/** One successful migration per config directory; never reapplies user policy. */
public final class NecRecoveryMigration {
    private static final Gson JSON = new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().create();
    public static final String STATE_DIR = "kewz-render-guard";
    public static final String MARKER = "nec-catch-gameloop-v1.done";
    public static final String BACKUP = "notenoughcrashes.pre-catch-gameloop-v1.json";

    private NecRecoveryMigration() {}

    public static void apply(Path configDir, boolean necPresent, Runnable synchronizeLoadedConfig,
                             Consumer<String> warn) {
        if (!necPresent) return;
        Path stateDir = configDir.resolve(STATE_DIR);
        Path marker = stateDir.resolve(MARKER);
        if (Files.exists(marker, LinkOption.NOFOLLOW_LINKS)) return;
        Path config = configDir.resolve("notenoughcrashes.json");
        Path backup = stateDir.resolve(BACKUP);
        Path temporary = null;
        try {
            if (!Files.isRegularFile(config, LinkOption.NOFOLLOW_LINKS)) {
                warn.accept("NEC config is absent or not a regular file; migration skipped.");
                return;
            }
            if (Files.exists(backup, LinkOption.NOFOLLOW_LINKS)) {
                warn.accept("NEC migration backup exists without a completion marker; leaving config untouched for review.");
                return;
            }
            byte[] original = Files.readAllBytes(config);
            JsonElement parsed;
            String originalText = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(original)).toString();
            try (JsonReader reader = new JsonReader(new StringReader(originalText))) {
                reader.setLenient(false);
                // Reading the adapter directly does not temporarily enable lenient parsing.
                parsed = JSON.getAdapter(JsonElement.class).read(reader);
                if (reader.peek() != JsonToken.END_DOCUMENT) throw new IllegalArgumentException("Trailing JSON content");
            }
            if (parsed == null || !parsed.isJsonObject()) throw new IllegalArgumentException("Expected JSON object");
            JsonObject object = parsed.getAsJsonObject();
            JsonElement setting = object.get("catchGameloop");
            if (setting == null || !setting.isJsonPrimitive() || !setting.getAsJsonPrimitive().isBoolean()) {
                throw new IllegalArgumentException("catchGameloop must be an existing boolean");
            }
            boolean change = setting.getAsBoolean();
            object.addProperty("catchGameloop", false);
            byte[] updated = change ? (JSON.toJson(object) + "\n").getBytes(StandardCharsets.UTF_8) : original;
            Files.createDirectories(stateDir);
            Files.write(backup, original, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
            // Refuse a concurrent edit instead of overwriting it with our earlier snapshot.
            if (!Arrays.equals(original, Files.readAllBytes(config))) throw new IllegalStateException("NEC config changed during migration");
            if (change) {
                temporary = Files.createTempFile(configDir, ".kewz-nec-", ".tmp");
                Files.write(temporary, updated);
                Files.move(temporary, config, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
                temporary = null;
            }
            // NEC main initialization precedes this client initializer. Its getCurrent()
            // constructs a record from this static field, so synchronize this launch too.
            synchronizeLoadedConfig.run();
            String record = "migration=nec-catch-gameloop-v1\noriginalSha256="
                    + HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(original))
                    + "\nresultSha256=" + HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(updated)) + "\n";
            Files.writeString(marker, record, StandardCharsets.UTF_8, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        } catch (Exception failure) {
            warn.accept("NEC one-time migration did not complete: " + failure.getClass().getSimpleName()
                    + ": " + failure.getMessage() + ". No arbitrary JSON repair attempted; retain any backup for review.");
        } finally {
            if (temporary != null) {
                try { Files.deleteIfExists(temporary); }
                catch (Exception failure) { warn.accept("Could not remove own NEC migration temporary file: " + temporary); }
            }
        }
    }
}
