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
/// 三、失焦 = 暂停，不是故障
/// 失焦时 <c>Main.UpdateAudio</c> 每帧 <c>PauseAll()</c>，而 FNA 的暂停会把动态声部的
/// 队列冲刷掉：<c>PendingBufferCount</c> 与 <c>BuffersQueued</c> 同时归零、<c>State</c> 变 Paused。
/// 早期版本据此判死并叠一层兜底曲，回焦后主题曲自行恢复，于是两首同时响。
/// 现在：失焦期间整段跳过；<c>IsPaused</c> 一律视为存活；兜底曲一旦发现主题恢复就立刻让位并停机。
/// </summary>
public class MenuMusicGuard : ModSystem {
    // 全部按真实时间计。原先按帧计数：在 60 fps 下"每秒 3 次"成立，
    // 但在 250 fps 的机器上实际每 0.08 秒就检查一次、"连续 1 秒无数据"只有 0.24 秒，
    // 缓冲还没补满就被判死，于是叠出兜底曲。
    const double CheckSeconds = 0.33;   // 每秒 3 次
    const double LogSeconds = 5.0;      // 5 秒一次状态行
    const int DeadLimit = 3;            // 连续 3 次确认无数据才动手（约 1 秒）
    const int FallbackSlot = 50;    // 默认菜单曲

    // 反射结果缓存：兜底期间每帧都要读一次声部状态，不能每帧都 GetField。
    static readonly FieldInfo VoiceField = typeof(ASoundEffectBasedAudioTrack).GetField(
        "_soundEffectInstance", BindingFlags.Instance | BindingFlags.NonPublic);

    double _checkClock;                 // 距上次检查的秒数
    double _logClock;                   // 距上次状态行的秒数
    int _lastWant = int.MinValue;
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

        // 失焦时原版每帧调用 audioSystem.PauseAll()，而 FNA 的暂停会冲刷动态声部队列：
        // PendingBufferCount 与 BuffersQueued 双双掉到 0，State 变成 Paused。
        // 这是暂停，不是故障 —— 回焦后 ResumeAll 与下一帧 Update 会自动补块。
        // 在这个窗口里做任何死亡判定都会把暂停误判成损坏，进而叠一层兜底曲；
        // 回焦后主题曲自行恢复，就变成两首一起响。所以整段跳过，连状态都不动。
        if (Main.instance is { IsActive: false })
            return;

        int want;
        try {
            want = MenuLoader.CurrentMenu?.Music ?? 0;
        }
        catch (Exception e) {
            LogOnce($"读取菜单主题音乐失败: {e.GetType().Name}: {e.Message}");
            return;
        }

        // 兜底要每帧顶住淡出（vanilla 每帧减 0.005），否则音量会周期性起伏。
        if (_fallbackOn)
            RunFallback(sys, want);

        // 用真实时间推进；单帧超过 0.2 秒说明在读盘/加载，别把时钟冲飞。
        double dt = gameTime.ElapsedGameTime.TotalSeconds;
        if (dt <= 0 || dt > 0.2)
            dt = 0.2;

        _checkClock += dt;
        _logClock += dt;

        if (_checkClock < CheckSeconds)
            return;

        _checkClock = 0;

        if (want != _lastWant) {
            // 换主题时把上一轮的兜底曲收掉，否则它会在后台一直响。
            if (_fallbackOn)
                StopFallback(sys);

            _lastWant = want;
            _dead = 0;
            _rebuildTried = false;
            _fallbackOn = false;
            _primedWant = int.MinValue;
            LogState(want, sys, "切换主题");
        }

        if (want <= 0 || want >= sys.AudioTracks.Length
            || Main.musicFade is null || want >= Main.musicFade.Length) {
            if (_logClock >= LogSeconds) {
                _logClock = 0;
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
                if (_logClock >= LogSeconds) {
                    _logClock = 0;
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
            // 暂停不是故障。失焦时原版会 PauseAll，回焦后 ResumeAll 会自行续播；
            // 这里若去 Reuse+Play，会把解码位置拨回开头，等于每秒重播一次。
            if (track.IsPaused) {
                _dead = 0;
                return;
            }

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

            if (_logClock >= LogSeconds) {
                _logClock = 0;
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
            if (_logClock >= LogSeconds) {
                _logClock = 0;
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

        if (_logClock >= LogSeconds) {
            _logClock = 0;
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

    void RunFallback(LegacyAudioSystem sys, int want) {
        if (FallbackSlot >= sys.AudioTracks.Length || Main.musicFade is null || FallbackSlot >= Main.musicFade.Length)
            return;

        // 主题本身就用 50 号曲时，"兜底"没有意义，也不该插手。
        if (want == FallbackSlot) {
            _fallbackOn = false;
            return;
        }

        // 兜底曲只能独占。主题音轨一旦恢复（在播、未暂停、有缓冲），立刻让位并把 50 号曲停掉；
        // 否则就是"主题曲 + 默认菜单曲"同时响 —— 回焦后最容易撞上这一幕。
        var theme = want > 0 && want < sys.AudioTracks.Length ? sys.AudioTracks[want] : null;
        if (theme is not null && theme.IsPlaying && !theme.IsPaused && !VoiceDiag(theme).Dead) {
            _fallbackOn = false;
            StopFallback(sys);
            Mod.Logger.Info($"[MenuMusicGuard] 主题音轨 want={want} 已恢复，撤销兜底曲 {FallbackSlot}。");
            return;
        }

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

    /// <summary>停掉兜底曲并把它的淡入值归零，避免回焦时被 ResumeAll 一并恢复。</summary>
    void StopFallback(LegacyAudioSystem sys) {
        if (Main.musicFade is not null && FallbackSlot < Main.musicFade.Length)
            Main.musicFade[FallbackSlot] = 0f;

        if (FallbackSlot >= sys.AudioTracks.Length)
            return;

        var track = sys.AudioTracks[FallbackSlot];
        if (track is null)
            return;

        try {
            if (track.IsPlaying || track.IsPaused)
                track.Stop(AudioStopOptions.Immediate);
        }
        catch (Exception e) {
            LogOnce($"停止兜底曲失败: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>按槽位反查模组音乐路径并重建音轨；仅在曲目对象为空或声部已死时调用。</summary>
    IAudioTrack TryRebuild(int slot, LegacyAudioSystem sys) {
        try {
            string path = FindMusicPath(slot);
            if (path is null) {
                LogOnce($"重建音轨 {slot} 失败: 找不到对应的音乐路径");
                return null;
            }

            // GetMusic 只在该槽位为空时才新建对象，所以必须先摘掉旧对象；
            // 但失败时要把它放回去，否则槽位永久为 null，vanilla 的 UpdateCommonTrack 会抛异常。
            var old = sys.AudioTracks[slot];
            sys.AudioTracks[slot] = null;

            IAudioTrack rebuilt;
            try {
                rebuilt = MusicLoader.GetMusic(path);
            }
            catch {
                sys.AudioTracks[slot] = old;
                throw;
            }

            if (rebuilt is null) {
                sys.AudioTracks[slot] = old;
                return null;
            }

            try {
                old?.Dispose();
            }
            catch {
                // 旧对象可能已经处于不可用状态，释放失败不影响新对象。
            }

            Mod.Logger.Info($"[MenuMusicGuard] 已按路径重建音轨 slot={slot} path={path} ok=True");
            return rebuilt;
        }
        catch (Exception e) {
            LogOnce($"重建音轨 {slot} 失败: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    static string FindMusicPath(int slot) {
        foreach (var mod in ModLoader.Mods) {
            if (mod is null)
                continue;

            // File 为 null 的模组（内置 ModLoader、从源码加载的模组）会让 GetFileNames()
            // 返回 null，直接 foreach 会抛 NRE —— 之前正是它把整条重建路径废掉的。
            System.Collections.Generic.List<string> names;
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

                string noExt = file.Substring(0, file.Length - ext.Length);
                string full = mod.Name + "/" + noExt;

                int slotOfFile;
                try {
                    slotOfFile = MusicLoader.GetMusicSlot(full);
                }
                catch {
                    continue;
                }

                if (slotOfFile == slot)
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
            if (VoiceField?.GetValue(se) is not DynamicSoundEffectInstance dyn)
                return new VoiceInfo { Text = "dyn=NULL", Dead = true };

            int pending = dyn.PendingBufferCount;
            string text = $"dynState={dyn.State} dynVol={dyn.Volume:0.000} pending={pending} disposed={dyn.IsDisposed}";

            // 暂停时原版不再提交缓冲，pending 会掉到 0 —— 这不是坏死，不能计入死亡。
            if (dyn.State == SoundState.Paused)
                return new VoiceInfo { Text = text + " (paused)", Dead = false };

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
        _checkClock = 0;
        _logClock = 0;
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
