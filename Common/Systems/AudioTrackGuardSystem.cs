using Terraria;
using Terraria.Audio;
using Terraria.ModLoader;

namespace TerrariaAmbience.Common.Systems;

/// <summary>
/// 原版 Terraria 的 <c>LegacyAudioSystem.UpdateCommonTrack</c> 缺少判空：
/// 当 <c>AudioTracks[i]</c> 为 null 时（音轨因内存不足创建失败，或刚被释放）会抛异常。
/// 该异常被 <c>Main.UpdateAudio</c> 的空 catch 吞掉，只做 <c>musicError++</c>；
/// 连续累计满 100 次就把 <c>Main.musicVolume</c> 静默置 0。
/// 而 <c>musicVolume == 0</c> 会让 <c>curMusic</c> 恒为 0，表现为
/// "玩一段时间后音乐永久消失，只有重启游戏才恢复"。
///
/// 这条 hook 只补判空，任何正常路径的行为都不变。
/// 同文件的 <c>UpdateCommonTrackTowardStopping</c> 本来就有判空，说明原版这里是疏漏。
/// </summary>
public class AudioTrackGuardSystem : ModSystem {
    public override void Load() {
        if (Main.dedServ)
            return;

        On_LegacyAudioSystem.UpdateCommonTrack += SafeUpdateCommonTrack;
    }

    public override void Unload() {
        On_LegacyAudioSystem.UpdateCommonTrack -= SafeUpdateCommonTrack;
    }

    float _lastMusicVolume = -1f;

    public override void PostUpdateEverything() {
        if (Main.dedServ)
            return;

        // 若音量在运行时被外部置零（原版的静默失败路径，或用户手动拉到 0），留一条可追踪的记录。
        float vol = Main.musicVolume;
        if (_lastMusicVolume > 0.01f && vol <= 0.0001f)
            Mod.Logger.Warn($"[AudioGuard] Main.musicVolume 由 {_lastMusicVolume:F3} 变为 0（curMusic={Main.curMusic}, newMusic={Main.newMusic}）。若是意外静音，重启游戏或在设置里重新拖动音乐音量即可恢复。");

        _lastMusicVolume = vol;
    }

    static void SafeUpdateCommonTrack(
        On_LegacyAudioSystem.orig_UpdateCommonTrack orig,
        LegacyAudioSystem self,
        bool active,
        int i,
        float totalVolume,
        ref float tempFade) {

        // 轨道缺失时把淡入值归零并跳过，等价于"这条曲子不存在"，而不是让异常冒到 Main.UpdateAudio。
        if (self.AudioTracks is null || i < 0 || i >= self.AudioTracks.Length || self.AudioTracks[i] is null) {
            tempFade = 0f;
            return;
        }

        orig(self, active, i, totalVolume, ref tempFade);
    }
}
