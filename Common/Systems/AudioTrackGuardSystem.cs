using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
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
        _mod = Mod;
        On_Main.Update += Main_Update;
        On_OGGAudioTrack.ReadAheadPutAChunkIntoTheBuffer += OggReadAhead;
    }

    public override void Unload() {
        On_LegacyAudioSystem.UpdateCommonTrack -= SafeUpdateCommonTrack;
        On_Main.Update -= Main_Update;
        On_OGGAudioTrack.ReadAheadPutAChunkIntoTheBuffer -= OggReadAhead;
    }

    // 注意：ModSystem.PostUpdateEverything 在主菜单里不会被调用（菜单只跑 Main.Update），
    // 想在菜单里观察就得挂 Main.Update，和 MenuMusicGuard 一样。
    void Main_Update(On_Main.orig_Update orig, Main self, GameTime gameTime) {
        orig(self, gameTime);

        if (Main.dedServ)
            return;

        float vol = Main.musicVolume;
        if (_lastMusicVolume > 0.01f && vol <= 0.0001f)
            Mod.Logger.Warn($"[AudioGuard] Main.musicVolume 由 {_lastMusicVolume:F3} 变为 0（curMusic={Main.curMusic}, newMusic={Main.newMusic}）。若是意外静音，重启游戏或在设置里重新拖动音乐音量即可恢复。");
        _lastMusicVolume = vol;

        bool debug = false;
        try { debug = ModContent.GetInstance<AmbientConfig>().debugInterface; }
        catch (Exception e) { Mod.Logger.Info($"[AudioProbe] config EX {e.GetType().Name}"); }

        if (!_probeAliveOnce) {
            _probeAliveOnce = true;
            Mod.Logger.Info($"[AudioProbe] alive debug={debug} menu={Main.gameMenu} sys={Main.audioSystem?.GetType().Name}");
        }

        if (debug) {
            ProbeMusic();
            ForceMusicAudible();
        }
    }

    // ---- 实验：把 Overhaul 之外的干扰清掉，看声音是否回来 ----
    // Overhaul 每帧对每个动态音轨做两件事：给声部挂低通（cutoff 被当成 0.1~1 Hz），
    // 以及往混响子混音写发送矩阵。两者都不改音量/状态，正好符合“状态健康但听不见”。
    // 这里在帧末把它们改成通带 + 单位阵；如果声音回来，就说明是这两者之一。
    static long _forceLastLog = -1000;
    static readonly Stopwatch ForceWatch = Stopwatch.StartNew();

    static unsafe void ForceMusicAudible() {
        try {
            if (Main.audioSystem is not LegacyAudioSystem sys || sys.AudioTracks is null)
                return;

            int i = Main.curMusic;
            if (i <= 0 || i >= sys.AudioTracks.Length)
                return;
            if (sys.AudioTracks[i] is not ASoundEffectBasedAudioTrack se)
                return;
            if (DynField?.GetValue(se) is not DynamicSoundEffectInstance dyn || dyn.handle == IntPtr.Zero)
                return;

            int rate = (int?)RateField?.GetValue(se) ?? 44100;
            int ch = Convert.ToInt32(ChannelsField?.GetValue(se)) == 2 ? 2 : 1;

            var dev = SoundEffect.Device();

            // 先记录干预前的真实状态，再动手改。
            string before = "?";
            try {
                var got = new float[ch * 2];
                FAudio.FAudioVoice_GetOutputMatrix(dyn.handle, dev.MasterVoice, (uint)ch, 2u, got);
                before = "[" + string.Join(",", Array.ConvertAll(got, v => v.ToString("0.###"))) + "]";
            }
            catch (Exception e) {
                before = "EX:" + e.GetType().Name;
            }

            var fp = new FAudio.FAudioFilterParameters {
                Type = FAudio.FAudioFilterType.FAudioLowPassFilter,
                Frequency = rate * 0.45f,   // 接近 Nyquist，等价于不滤
                OneOverQ = 1f,
            };
            uint hrFilter = FAudio.FAudioVoice_SetFilterParameters(dyn.handle, ref fp, 0);

            var m = ch == 2 ? new float[] { 1f, 0f, 0f, 1f } : new float[] { 1f, 1f };
            uint hrMatrix;
            fixed (float* pm = m) {
                hrMatrix = FAudio.FAudioVoice_SetOutputMatrix(dyn.handle, dev.MasterVoice, (uint)ch, 2u, (nint)pm, 0);
            }

            long now = ForceWatch.ElapsedMilliseconds;
            if (now - _forceLastLog >= 5000) {
                _forceLastLog = now;

                string readback = "?";
                try {
                    var got = new float[ch * 2];
                    FAudio.FAudioVoice_GetOutputMatrix(dyn.handle, dev.MasterVoice, (uint)ch, 2u, got);
                    readback = "[" + string.Join(",", Array.ConvertAll(got, v => v.ToString("0.###"))) + "]";
                }
                catch (Exception e) {
                    readback = "EX:" + e.GetType().Name;
                }

                _mod.Logger.Info($"[AudioFix] slot={i} rate={rate} ch={ch} matrixBefore={before} " +
                    $"setFilter=0x{hrFilter:X} setMatrix=0x{hrMatrix:X} matrixAfter={readback}");
            }
        }
        catch (Exception e) {
            if (ForceWatch.ElapsedMilliseconds - _forceLastLog >= 5000) {
                _forceLastLog = ForceWatch.ElapsedMilliseconds;
                _mod?.Logger.Info($"[AudioFix] EX {e.GetType().Name}: {e.Message}");
            }
        }
    }

    float _lastMusicVolume = -1f;
    readonly Stopwatch _probeWatch = Stopwatch.StartNew();
    double _lastProbeAt = -10;
    int _lastProbeMusic = int.MinValue;
    bool _probeResolved;
    static bool _probeAliveOnce;
    static Dictionary<int, string> _slotNames;

    /// <summary>
    /// 排查"某个群系没有音乐"。三种可能分得很开：
    /// new=0 → 压根没判定出曲目（群系/SceneEffect 侧）；track=NULL → 槽位注册了但音轨没建起来（音乐包侧）；
    /// fade 长期 0 且 playing=False → 音轨在、但没被拉起。
    /// 同时打印槽位对应的资源名，便于直接判断是哪首曲子。
    /// </summary>
    void ProbeMusic() {
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

    // 直接量“交给声部的样本是否为零”。这是唯一能把"解码没产出"与"输出被掐"分开的证据。
    static Mod _mod;
    static readonly System.Reflection.FieldInfo BufField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_bufferToSubmit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static int _bufChunks;
    static double _bufSumSq;
    static long _bufSamples;
    static long _bufLastLog = -1;
    static readonly Stopwatch BufWatch = new();

    static void OggReadAhead(On_OGGAudioTrack.orig_ReadAheadPutAChunkIntoTheBuffer orig, Terraria.Audio.OGGAudioTrack self) {
        orig(self);

        try {
            if (!ModContent.GetInstance<AmbientConfig>().debugInterface)
                return;

            if (BufField?.GetValue(self) is not byte[] buf)
                return;

            double sum = 0;
            for (int i = 0; i + 1 < buf.Length; i += 2) {
                short v = (short)(buf[i] | (buf[i + 1] << 8));
                double f = v / 32768.0;
                sum += f * f;
            }
            _bufSumSq += sum;
            _bufSamples += buf.Length / 2;
            _bufChunks++;

            if (_bufLastLog < 0) {
                BufWatch.Start();
                _bufLastLog = 0;
            }
            long now = BufWatch.ElapsedMilliseconds;
            if (now - _bufLastLog >= 4000 && _bufSamples > 0) {
                _bufLastLog = now;
                double rms = Math.Sqrt(_bufSumSq / _bufSamples);
                _mod.Logger.Info($"[AudioBuf] rms={rms:0.00000} chunks={_bufChunks} samples={_bufSamples}");
                _bufSumSq = 0;
                _bufSamples = 0;
                _bufChunks = 0;
            }
        }
        catch {
            // 诊断代码不允许影响播放。
        }
    }

    static readonly System.Reflection.FieldInfo DynField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_soundEffectInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo RateField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_sampleRate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo ChannelsField = typeof(ASoundEffectBasedAudioTrack)
        .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// 声部内部状态：pending/queued 才是"缓冲区里到底有没有音频数据"的硬指标。
    /// realVol 直接从 FAudio 读实际音量，用来识别"FNA 缓存的 Volume 与实际声部不一致"。
    /// </summary>
    static string VoiceExtra(IAudioTrack track) {
        if (track is not ASoundEffectBasedAudioTrack se)
            return string.Empty;

        try {
            if (DynField?.GetValue(se) is not DynamicSoundEffectInstance dyn)
                return " | dyn=NULL";

            float realVol = -1f;
            uint queued = 0;
            string err = string.Empty;

            try {
                if (dyn.handle != IntPtr.Zero) {
                    FAudio.FAudioVoice_GetVolume(dyn.handle, out realVol);
                    FAudio.FAudioSourceVoice_GetState(dyn.handle, out var state, 0u);
                    queued = state.BuffersQueued;
                }
                else {
                    err = " handle=0";
                }
            }
            catch {
                err = " faudio=EX";
            }

            string filter = "filt=?";
            string matrix = "dry=?";
            try {
                if (dyn.handle != IntPtr.Zero) {
                    // 声部上挂的低通：Overhaul 的 LowPassFilteringSystem 会把
                    // 1 - intensity*0.9（0.1~1.0）直接当 Hz 传给 FAudio。
                    FAudio.FAudioVoice_GetFilterParameters(dyn.handle, out var fp);
                    filter = $"filt={fp.Type}@{fp.Frequency:0.###}Hz/q={fp.OneOverQ:0.##}";

                    var dev = SoundEffect.Device();
                    int ch = Convert.ToInt32(ChannelsField?.GetValue(se)) == 2 ? 2 : 1;
                    var m = new float[ch * 2];
                    FAudio.FAudioVoice_GetOutputMatrix(dyn.handle, dev.MasterVoice, (uint)ch, 2u, m);
                    matrix = "dry=[" + string.Join(",", Array.ConvertAll(m, v => v.ToString("0.###"))) + "]";
                }
            }
            catch {
                filter = "filt=EX";
            }

            return $" | dynVol={dyn.Volume:0.000} realVol={realVol:0.000} pending={dyn.PendingBufferCount}" +
                   $" queued={queued}" +
                   $" rate={(int?)RateField?.GetValue(se)} ch={ChannelsField?.GetValue(se)}{err}" +
                   $" {filter} {matrix}";
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
