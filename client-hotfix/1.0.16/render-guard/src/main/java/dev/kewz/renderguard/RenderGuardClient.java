package dev.kewz.renderguard;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.loader.api.FabricLoader;

public final class RenderGuardClient implements ClientModInitializer {
    @Override
    public void onInitializeClient() {
        FabricLoader loader = FabricLoader.getInstance();
        NecRecoveryMigration.apply(loader.getConfigDir(), loader.isModLoaded("notenoughcrashes"),
                RenderGuardClient::synchronizeNec,
                message -> System.getLogger("KewzRenderGuard").log(System.Logger.Level.WARNING, message));
    }

    public static void synchronizeNec() {
        try {
            Class<?> config = Class.forName("fudge.notenoughcrashes.config.NecMidnightConfig",
                    false, RenderGuardClient.class.getClassLoader());
            Field field = config.getField("catchGameloop");
            if (field.getType() != boolean.class || !Modifier.isStatic(field.getModifiers())) {
                throw new IllegalStateException("Unexpected NEC catchGameloop field type");
            }
            field.setBoolean(null, false);
        } catch (ReflectiveOperationException | LinkageError failure) {
            throw new IllegalStateException("Could not synchronize NEC's loaded catchGameloop setting", failure);
        }
    }
}
