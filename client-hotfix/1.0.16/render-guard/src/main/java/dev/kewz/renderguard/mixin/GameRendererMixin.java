package dev.kewz.renderguard.mixin;

import net.minecraft.class_310;
import net.minecraft.class_757;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Minecraft 1.21.1 intermediary: GameRenderer; field_4015 = client. */
@Mixin(value = class_757.class, remap = false)
public abstract class GameRendererMixin {
    @Shadow @Final class_310 field_4015;

    // renderWorld(RenderTickCounter): cancel before lightmap/camera/profiler work.
    @Inject(method = "method_3188(Lnet/minecraft/class_9779;)V",
            at = @At("HEAD"), cancellable = true, require = 1, expect = 1, allow = 1,
            remap = false)
    private void kewz$skipWorldWithoutPlayer(CallbackInfo ci) {
        // field_1724 = player; field_1687 = world. Do not change either field.
        if (field_4015.field_1724 == null || field_4015.field_1687 == null) {
            ci.cancel();
        }
    }
}
