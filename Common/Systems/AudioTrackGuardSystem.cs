using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using Terraria;
using Terraria.Audio;
using Terraria.ModLoader;
using TerrariaAmbience.Core;

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
    readonly Stopwatch _probeWatch = Stopwatch.StartNew();
    double _lastProbeAt = -10;
    int _lastProbeMusic = int.MinValue;
    bool _probeResolved;
    static Dictionary<int, string> _slotNames;

    public override void PostUpdateEverything() {
        if (Main.dedServ)
            return;

        // 若音量在运行时被外部置零（原版的静默失败路径，或用户手动拉到 0），留一条可追踪的记录。
        float vol = Main.musicVolume;
        if (_lastMusicVolume > 0.01f && vol <= 0.0001f)
            Mod.Logger.Warn($"[AudioGuard] Main.musicVolume 由 {_lastMusicVolume:F3} 变为 0（curMusic={Main.curMusic}, newMusic={Main.newMusic}）。若是意外静音，重启游戏或在设置里重新拖动音乐音量即可恢复。");

        _lastMusicVolume = vol;

        if (ModContent.GetInstance<AmbientConfig>().debugInterface)
            ProbeMusic();
    }

    /// <summary>
    /// 排查"某个群系没有音乐"。三种可能分得很开：
    /// new=0 → 压根没判定出曲目（群系/SceneEffect 侧）；track=NULL → 槽位注册了但音轨没建起来（音乐包侧）；
    /// fade 长期 0 且 playing=False → 音轨在、但没被拉起。
    /// 同时打印槽位对应的资源名，便于直接判断是哪首曲子。
    /// </summary>
    void ProbeMusic() {
        if (Main.gameMenu)
            return;
        if (Main.audioSystem is not LegacyAudioSystem sys || sys.AudioTracks is null || Main.musicFade is null)
            return;

        int next = Main.newMusic;
        double now = _probeWatch.Elapsed.TotalSeconds;
        bool changed = next != _lastProbeMusic;
        if (!changed && now - _lastProbeAt < 3.0)
            return;

        _lastProbeAt = now;
        _lastProbeMusic = next;

        if (!_probeResolved) {
            _probeResolved = true;
            Mod.Logger.Info($"[AudioProbe] tracks={sys.AudioTracks.Length} fades={Main.musicFade.Length} " +
                $"vol={Main.musicVolume:0.00} master={SafeMaster()}");
        }

        var track = next > 0 && next < sys.AudioTracks.Length ? sys.AudioTracks[next] : null;
        float fade = next >= 0 && next < Main.musicFade.Length ? Main.musicFade[next] : -1f;

        Mod.Logger.Info($"[AudioProbe] cur={Main.curMusic} new={next} fade={fade:0.00} " +
            $"track={(track is null ? "NULL" : track.GetType().Name)} " +
            $"playing={track?.IsPlaying} paused={track?.IsPaused} " +
            $"name={SlotName(next)}{VoiceExtra(track)}");
    }

    static readonly System.Reflection.FieldInfo DynField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_soundEffectInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo RateField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_sampleRate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo ChannelsField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// 声部内部状态：pending/queued 才是"缓冲区里到底有没有音频数据"的硬指标。
    /// playing=True 但 pending=0 说明解码侧没产出，听感就是静音。
    /// </summary>
    static string VoiceExtra(IAudioTrack track) {
        if (track is not ASoundEffectBasedAudioTrack se)
            return string.Empty;

        try {
            if (DynField?.GetValue(se) is not DynamicSoundEffectInstance dyn)
                return " | dyn=NULL";

            uint queued = 0;
            string getState = string.Empty;
            try {
                if (dyn.handle != IntPtr.Zero) {
                    FAudio.FAudioSourceVoice_GetState(dyn.handle, out var state, 256u);
                    queued = state.BuffersQueued;
                }
                else {
                    getState = " handle=0";
                }
            }
            catch {
                getState = " getState=EX";
            }

            return $" | dynVol={dyn.Volume:0.000} pending={dyn.PendingBufferCount} queued={queued}" +
                   $" rate={(int?)RateField?.GetValue(se)} ch={ChannelsField?.GetValue(se)}{getState}";
        }
        catch (Exception e) {
            return $" | voice EX {e.GetType().Name}";
        }
    }

    /// <summary>槽位 → "模组/资源路径"，只建一次。把 new=145 翻译成"这是哪首曲子"。</summary>
    static string SlotName(int slot) {
        if (slot <= 0)
            return "-";

        if (_slotNames is null) {
            var map = new Dictionary<int, string>();
            foreach (var mod in ModLoader.Mods) {
                if (mod is null)
                    continue;

                List<string> names;
                try {
                    names = mod.GetFileNames();
                }
                catch {
                    continue;
                }

                if (names is null)
                    continue;

                foreach (string raw in names) {
                    string file = raw.Replace('\\', '/');
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".ogg" or ".mp3" or ".wav"))
                        continue;
                    if (!file.StartsWith("Music/", StringComparison.OrdinalIgnoreCase)
                        && !file.Contains("/Music/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string full = mod.Name + "/" + file.Substring(0, file.Length - ext.Length);
                    try {
                        int s = MusicLoader.GetMusicSlot(full);
                        if (s > 0)
                            map[s] = full;
                    }
                    catch {
                        // 单个模组的资源名解析失败不影响整体映射。
                    }
                }
            }

            _slotNames = map;
        }

        return _slotNames.TryGetValue(slot, out var name) ? name : $"<vanilla {slot}>";
    }

    static string SafeMaster() {
        try {
            return $"{Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume:0.00}";
        }
        catch {
            return "?";
        }
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
