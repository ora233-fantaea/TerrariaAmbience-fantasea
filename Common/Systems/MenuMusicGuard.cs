using System;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAmbience.Common.Systems;

/// <summary>
/// 主菜单音乐守护。
///
/// 一、为什么需要“硬重启”
/// 退出世界后，菜单主题的曲目会处于“标志说在播、实际没声音”的状态。
/// FNA 的 <c>SoundEffectInstance.State</c> 对 dynamic 实例跳过
/// “BuffersQueued == 0 就 Stop”的自检，所以 <c>IsPlaying</c> 不可信。
/// 而 <c>Reuse()</c> 只把解码位置拨回开头、<b>不清空</b>已排队的缓冲，
/// 因此对这类声部做 <c>Reuse + Play</c> 等于什么都没做。
/// 只有 <c>Stop(Immediate)</c> 会冲刷队列，之后 <c>Reuse + Play</c> 才真正重新出声。
///
/// 二、为什么“只重启一次”
/// <c>ASoundEffectBasedAudioTrack.Update()</c> 每帧在
/// <c>PendingBufferCount &lt; 8</c> 时提交两块，所以**健康播放时该计数几乎恒为 8**。
/// 早期版本把“计数长时间不动”当成停滞，结果每 0.3 秒重启一次健康曲目，
/// 表现为“播 1~2 秒又重头开始”，反复几次后还触发了兜底换曲。
/// 现在只在“确实没有声部/没有数据”时才动手，且每个主题只做一轮升级：
/// 硬重启 → 重建音轨 → 兜底，绝不循环。
/// </summary>
public class MenuMusicGuard : ModSystem {
    const int CheckInterval = 20;   // 每秒 3 次
    const int LogInterval = 300;    // 5 秒一次状态行
    const int DeadLimit = 3;        // 连续 3 次确认无数据才动手（约 1 秒）
    const int FallbackSlot = 50;    // 默认菜单曲

    int _tick;
    int _lastWant = int.MinValue;
    int _lastLogTick = -10000;
    int _dead;
    bool _rebuildTried;
    bool _fallbackOn;
    int _primedWant = int.MinValue;
    string _lastError = string.Empty;

    public override void Load() {
        if (Main.dedServ)
            return;

        On_Main.Update += Main_Update;
    }

    public override void Unload() {
        On_Main.Update -= Main_Update;
    }

    void Main_Update(On_Main.orig_Update orig, Main self, GameTime gameTime) {
        orig(self, gameTime);

        if (Main.dedServ || !Main.gameMenu) {
            ResetState();
            return;
        }

        if (Main.audioSystem is not LegacyAudioSystem sys || sys.AudioTracks is null)
            return;

        // 兜底要每帧顶住淡出（vanilla 每帧减 0.005），否则音量会周期性起伏。
        if (_fallbackOn)
            RunFallback(sys);

        _tick++;
        if (_tick % CheckInterval != 0)
            return;

        int want;
        try {
            want = MenuLoader.CurrentMenu?.Music ?? 0;
        }
        catch (Exception e) {
            LogOnce($"读取菜单主题音乐失败: {e.GetType().Name}: {e.Message}");
            return;
        }

        if (want != _lastWant) {
            _lastWant = want;
            _dead = 0;
            _rebuildTried = false;
            _fallbackOn = false;
            _primedWant = int.MinValue;
            LogState(want, sys, "切换主题");
        }

        if (want <= 0 || want >= sys.AudioTracks.Length
            || Main.musicFade is null || want >= Main.musicFade.Length) {
            if (_tick - _lastLogTick >= LogInterval) {
                _lastLogTick = _tick;
                Mod.Logger.Warn($"[MenuMusicGuard] 主题声明的音乐槽位不可用 want={want} " +
                    $"tracks={(sys.AudioTracks?.Length ?? -1)} fades={(Main.musicFade?.Length ?? -1)} " +
                    $"menu={MenuLoader.CurrentMenu?.GetType().Name}");
            }
            return;
        }

        var track = sys.AudioTracks[want];
        if (track is null) {
            track = TryRebuild(want, sys);
            if (track is null) {
                if (_tick - _lastLogTick >= LogInterval) {
                    _lastLogTick = _tick;
                    Mod.Logger.Warn($"[MenuMusicGuard] 曲目对象为空且无法重建 want={want} " +
                        $"menu={MenuLoader.CurrentMenu?.GetType().Name}");
                }
                return;
            }
        }

        float fade = Main.musicFade[want];

        // 第一步：进入菜单（或换主题）后，对 SE 类音轨做一次硬重启。
        // 这一步冲刷掉退出世界时遗留的旧队列，是“重新出声”的关键；
        // 每个主题只做一次，不会造成循环重播。
        if (_primedWant != want) {
            _primedWant = want;
            if (track is ASoundEffectBasedAudioTrack) {
                HardRestart(track, want, fade, "首次进菜单");
                LogState(want, sys, "已硬重启");
                return;
            }
        }

        if (!track.IsPlaying) {
            bool started = TryStart(track, want, fade);
            _dead = started ? 0 : _dead + 1;

            if (!started && _dead >= DeadLimit && !_rebuildTried) {
                _rebuildTried = true;
                var rebuilt = TryRebuild(want, sys);
                if (rebuilt is not null && TryStart(rebuilt, want, fade)) {
                    track = rebuilt;
                    _dead = 0;
                }
            }

            if (_dead >= DeadLimit * 2 && !_fallbackOn) {
                _fallbackOn = true;
                Mod.Logger.Warn($"[MenuMusicGuard] want={want} 无法起播，回退到默认菜单曲 {FallbackSlot}。");
            }

            if (_tick - _lastLogTick >= LogInterval) {
                _lastLogTick = _tick;
                LogState(want, sys, started ? "已起播" : "起播失败");
            }
            return;
        }

        // 第二步：判断是否真的没有声部数据。
        // 只看硬指标：句柄为空、已释放、或 pending 与 queued 同时为 0。
        // 故意不看“pending 是否变化”——健康播放时它本来就长期停在 8。
        var diag = VoiceDiag(track);

        if (!diag.Dead) {
            _dead = 0;
            if (_tick - _lastLogTick >= LogInterval) {
                _lastLogTick = _tick;
                LogState(want, sys, "正常播放", diag);
            }
            return;
        }

        _dead++;

        if (_dead >= DeadLimit) {
            if (!_rebuildTried) {
                _rebuildTried = true;
                var rebuilt = TryRebuild(want, sys);
                if (rebuilt is not null) {
                    track = rebuilt;
                    HardRestart(track, want, fade, "重建后");
                    Mod.Logger.Info($"[MenuMusicGuard] 已重建音轨 want={want}");
                }
            }
            else if (!_fallbackOn) {
                _fallbackOn = true;
                Mod.Logger.Warn($"[MenuMusicGuard] want={want} 重建后仍无声部数据，回退到默认菜单曲 {FallbackSlot}。");
            }
        }

        if (_tick - _lastLogTick >= LogInterval) {
            _lastLogTick = _tick;
            LogState(want, sys, "无声部数据", diag);
        }
    }

    bool TryStart(IAudioTrack track, int slot, float fade) {
        try {
            track.Reuse();
            track.SetVariable(MusicID.Sets.SkipsVolumeRemap[slot] ? "VolumeDirect" : "Volume",
                Math.Max(fade, 0.01f) * Main.musicVolume);
            track.Play();
            return track.IsPlaying;
        }
        catch (Exception e) {
            LogOnce($"起播音轨 {slot} 抛异常: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    void HardRestart(IAudioTrack track, int slot, float fade, string reason) {
        try {
            try {
                track.Stop(AudioStopOptions.Immediate);
            }
            catch {
                // 声部已失效时 Stop 可能抛异常，继续走重启流程。
            }

            track.Reuse();
            track.SetVariable(MusicID.Sets.SkipsVolumeRemap[slot] ? "VolumeDirect" : "Volume",
                Math.Max(fade, 0.01f) * Main.musicVolume);
            track.Play();
            Mod.Logger.Info($"[MenuMusicGuard] 硬重启 want={slot} reason={reason} playing={track.IsPlaying}");
        }
        catch (Exception e) {
            LogOnce($"强制重播音轨 {slot} 失败: {e.GetType().Name}: {e.Message}");
        }
    }

    void RunFallback(LegacyAudioSystem sys) {
        if (FallbackSlot >= sys.AudioTracks.Length || Main.musicFade is null || FallbackSlot >= Main.musicFade.Length)
            return;

        var track = sys.AudioTracks[FallbackSlot];
        if (track is null)
            return;

        // 顶住 vanilla 的淡出：它每帧减 0.005，这里每次检查补回 1。
        Main.musicFade[FallbackSlot] = 1f;

        if (track.IsPlaying)
            return;

        try {
            track.Reuse();
            track.SetVariable(MusicID.Sets.SkipsVolumeRemap[FallbackSlot] ? "VolumeDirect" : "Volume",
                Main.musicVolume);
            track.Play();
        }
        catch (Exception e) {
            LogOnce($"兜底曲目起播失败: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>按槽位反查模组音乐路径并重建音轨；仅在曲目对象为空或声部已死时调用。</summary>
    IAudioTrack TryRebuild(int slot, LegacyAudioSystem sys) {
        try {
            string path = FindMusicPath(slot);
            if (path is null)
                return null;

            try {
                sys.AudioTracks[slot]?.Dispose();
            }
            catch {
                // 旧对象可能已经处于不可用状态，释放失败不影响后续重建。
            }

            sys.AudioTracks[slot] = null;
            var rebuilt = MusicLoader.GetMusic(path);
            Mod.Logger.Info($"[MenuMusicGuard] 已按路径重建音轨 slot={slot} path={path} ok={rebuilt is not null}");
            return rebuilt;
        }
        catch (Exception e) {
            LogOnce($"重建音轨 {slot} 失败: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    static string FindMusicPath(int slot) {
        foreach (var mod in ModLoader.Mods) {
            foreach (string raw in mod.GetFileNames()) {
                string file = raw.Replace('\\', '/');
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".ogg" or ".mp3" or ".wav"))
                    continue;
                if (!file.StartsWith("Music/", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains("/Music/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string noExt = file.Substring(0, file.Length - ext.Length);
                string full = mod.Name + "/" + noExt;
                if (MusicLoader.GetMusicSlot(full) == slot)
                    return full;
            }
        }

        return null;
    }

    struct VoiceInfo {
        public string Text;
        public bool Dead;
    }

    /// <summary>读取动态声部的原生状态。Cue 音轨（XACT）不适用，视为存活。</summary>
    static VoiceInfo VoiceDiag(IAudioTrack track) {
        if (track is not ASoundEffectBasedAudioTrack se)
            return new VoiceInfo { Text = "kind=cue", Dead = false };

        try {
            var field = typeof(ASoundEffectBasedAudioTrack).GetField(
                "_soundEffectInstance", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(se) is not DynamicSoundEffectInstance dyn)
                return new VoiceInfo { Text = "dyn=NULL", Dead = true };

            int pending = dyn.PendingBufferCount;
            string text = $"dynState={dyn.State} dynVol={dyn.Volume:0.000} pending={pending} disposed={dyn.IsDisposed}";

            uint queued = 0;
            bool hasHandle = dyn.handle != IntPtr.Zero;
            if (hasHandle) {
                try {
                    FAudio.FAudioSourceVoice_GetState(dyn.handle, out var state, 256u);
                    queued = state.BuffersQueued;
                }
                catch {
                    text += " getState=EX";
                }
            }
            else {
                text += " handle=0";
            }

            // 只有“没有句柄 / 已释放 / 队列彻底空”才算死。
            // pending 长期为 8 是健康播放的正常形态，不能当作停滞。
            bool dead = !hasHandle || dyn.IsDisposed || (pending == 0 && queued == 0);

            text += $" queued={queued} dead={dead}";
            return new VoiceInfo { Text = text, Dead = dead };
        }
        catch (Exception e) {
            return new VoiceInfo { Text = $"diag EX {e.GetType().Name}", Dead = false };
        }
    }

    void LogState(int want, LegacyAudioSystem sys, string tag, VoiceInfo diag = default) {
        try {
            var track = want > 0 && want < sys.AudioTracks.Length ? sys.AudioTracks[want] : null;
            float fade = Main.musicFade is not null && want >= 0 && want < Main.musicFade.Length
                ? Main.musicFade[want] : -1f;

            string voice = string.IsNullOrEmpty(diag.Text) ? VoiceDiag(track).Text : diag.Text;

            Mod.Logger.Info($"[MenuMusicGuard] {tag} menu={MenuLoader.CurrentMenu?.GetType().Name} want={want} " +
                $"track={(track is null ? "NULL" : track.GetType().Name)} playing={track?.IsPlaying} " +
                $"stopped={track?.IsStopped} fade={fade:0.00} cur={Main.curMusic} new={Main.newMusic} " +
                $"musicVolume={Main.musicVolume:0.00} master={SafeMasterVolume():0.00} " +
                $"wavePrepared={sys.WaveBank?.IsPrepared} fallback={_fallbackOn} " +
                $"replayDelay={sys.MusicReplayDelay} active={Main.instance?.IsActive} | {voice}");
        }
        catch (Exception e) {
            LogOnce($"记录状态失败: {e.GetType().Name}: {e.Message}");
        }
    }

    static float SafeMasterVolume() {
        try {
            return SoundEffect.MasterVolume;
        }
        catch {
            return -1f;
        }
    }

    void ResetState() {
        _tick = 0;
        _dead = 0;
        _rebuildTried = false;
        _fallbackOn = false;
        _primedWant = int.MinValue;
        _lastWant = int.MinValue;
    }

    void LogOnce(string message) {
        if (_lastError == message)
            return;
        _lastError = message;
        Mod.Logger.Warn($"[MenuMusicGuard] {message}");
    }
}
