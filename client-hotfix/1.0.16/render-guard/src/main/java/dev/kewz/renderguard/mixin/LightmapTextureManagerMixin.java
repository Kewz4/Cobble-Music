package dev.kewz.renderguard.mixin;

import net.minecraft.class_310;
import net.minecraft.class_765;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Minecraft 1.21.1 intermediary: LightmapTextureManager; field_4137 = client. */
@Mixin(value = class_765.class, remap = false)
public abstract class LightmapTextureManagerMixin {
    @Shadow @Final private class_310 field_4137;

    // update(float): keep dirty and profiler state intact when a frame is skipped.
    @Inject(method = "method_3313(F)V", at = @At("HEAD"), cancellable = true,
            require = 1, expect = 1, allow = 1, remap = false)
    private void kewz$skipLightmapWithoutPlayer(CallbackInfo ci) {
        if (field_4137.field_1724 == null || field_4137.field_1687 == null) {
            ci.cancel();
        }
    }
}
