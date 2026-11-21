using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;
using TerrariaAmbience.Core;
using TerrariaAmbience.Sounds.SoundFilters.FAudioHacks;

namespace TerrariaAmbience.Sounds.SoundFilters;

public static class FilterHelpers {
    static bool showWarnings => ModContent.GetInstance<AmbientConfig>().showInfoAndWarnings;

    // 每条音源的滤波失败会在每 tick 重复触发；只在首次提示，避免聊天框被刷屏。
    static readonly HashSet<string> _warnedFilters = [];

    static void WarnOnce(string filter) {
        if (!showWarnings) return;
        if (!_warnedFilters.Add(filter)) return;

        Main.NewText(string.Format(FILTER_ISSUE_WARNING, filter), Color.Red);
    }
    public const string FILTER_ISSUE_WARNING = "Error applying sound filter for {0}. Check the pins in the [c/FFFF00:#bug-reports] channel in the [c/5865F2:Discord] server in the top left of the main menu.";
    public const string FILTER_SAVED_GAME = "Recovered from fatal error in {0}. If you are seeing this message, please report this in our [c/5865F2:Discord] server in the top left of the main menu.";
    public static SoundEffectInstance ApplyReverb(this SoundEffectInstance instance, FilterParams param = default) {
        if (param.ReverbGain < 0) {
            if (showWarnings)
                Main.NewText(string.Format(FILTER_SAVED_GAME, "Reverb"));
            return instance;
        }

        try {
            // 混响关闭时不必把子混音建起来：连 voice 都不创建，这条路径就完全不存在。
            if (!param.ReverbEnabled && !FAudioReverbController.IsInitialized)
                return instance;

            FAudioReverbController.RvInit();

            FAudioReverbController.ApplyCustomReverb(instance, param.ReverbGain, param);

            return instance;
        }
        catch {
            WarnOnce("Reverb");
            return instance;
        }
    }
    public static SoundEffectInstance ApplyFiltersQuick(this SoundEffectInstance instance, FilterParams param) {
        if (param.ReverbGain > 0)
            instance.ApplyReverb(param);
        if (param.LowPassEnabled)
            instance.ApplyLowPassFilter(param.LowPassIntensity);
        if (param.BandPassEnabled)
            instance.ApplyBandPassFilter(param.BandPassIntensity);

        return instance;
    }
    public static SoundEffectInstance ApplyLowPassFilter(this SoundEffectInstance instance, float cutoff) {
        if (cutoff < 0) {
            if (showWarnings)
                Main.NewText(string.Format(FILTER_SAVED_GAME, "LowPass"));
            return instance;
        }
        try {
            cutoff = MathHelper.Clamp(cutoff, 0f, 1f);

            instance.INTERNAL_applyLowPassFilter(cutoff);
            return instance;
        }
        catch {
            WarnOnce("LowPass");
            return instance;
        }
    }
    public static SoundEffectInstance ApplyHighPassFilter(this SoundEffectInstance instance, float cutoff) {
        if (cutoff < 0) {
            if (showWarnings)
                Main.NewText(string.Format(FILTER_SAVED_GAME, "HighPass"));
            return instance;
        }
        try {
            cutoff = MathHelper.Clamp(cutoff, 0f, 1f);

            instance.INTERNAL_applyHighPassFilter(cutoff);
            return instance;
        }
        catch {
            WarnOnce("HighPass");
            return instance;
        }
    }
    public static SoundEffectInstance ApplyBandPassFilter(this SoundEffectInstance instance, float center) {
        if (center < 0) {
            if (showWarnings)
                Main.NewText(string.Format(FILTER_SAVED_GAME, "BandPass"));
            return instance;
        }
        try {
            center = MathHelper.Clamp(center, -1f, 1f);

            instance.INTERNAL_applyBandPassFilter(center);
            return instance;
        }
        catch {
            WarnOnce("BandPass");
            return instance;
        }
    }
}
