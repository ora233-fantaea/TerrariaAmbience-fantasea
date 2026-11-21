using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework.Audio;
using Terraria;
using Terraria.ModLoader;
using TerrariaAmbienceAPI.Content;

namespace TerrariaAmbience.Sounds.Streaming;

/// <summary>
/// 流式环境床。公开面与 TerrariaAmbienceAPI 的 <c>ModAmbience</c> 对齐，
/// 便于在 <c>AmbientHandler</c> 里逐条替换而不牵动下游。
///
/// 线程约定（不可违反）：
/// <list type="bullet">
/// <item>解码与文件读取只发生在后台线程，持有 <see cref="OggStream"/>。</item>
/// <item><c>DynamicSoundEffectInstance</c> 的创建、SubmitBuffer、Play、Stop、Dispose
/// 只在主线程，FNA 的 <c>ThreadCheck</c> 会因为跨线程调用直接抛异常。</item>
/// <item>两边的唯一交接物是 <see cref="BlockQueue"/>，加锁粒度是「块」。</item>
/// </list>
///
/// 任何一环失败都只会把本对象标记为 <see cref="Failed"/>，不向调用方抛异常，
/// 由上层决定是否降级到常驻 <c>SoundEffect</c>。
/// </summary>
public sealed class StreamedAmbience : IDisposable {
    /// <summary>同时在飞的缓冲块数。3 块 ≈ 300 ms 余量。</summary>
    private const int TargetPendingBuffers = 3;

    /// <summary>队列容量。比在飞块数多一块，留给解码线程提前排队的余地。</summary>
    private const int QueueCapacity = 4;

    /// <summary>单块时长。100 ms 是解码成本与延迟的折中。</summary>
    private const double BlockSeconds = 0.1;

    private const int WorkerJoinTimeoutMs = 500;

    /// <summary>
    /// 同时活跃的流上限。单条流常驻约 75 KB（48 kHz 立体声、4 块 × 100 ms），
    /// 所以上限给得宽一些：8 条也才 600 KB，而现实中同时最多约 5 条
    /// （森林昼夜交界 2 条 + 风 2 条 + 雨 1 条）。超过后新的音轨保持静默，等槽位再起。
    /// </summary>
    public static int MaxActiveStreams = 8;

    /// <summary>
    /// 配置关闭流式时置真：所有音轨不启动解码线程，直接走常驻兜底。
    /// 这是唯一的一条「关掉流式」路径，不额外维护第二套实现。
    /// </summary>
    public static bool ForceResident;

    private static readonly List<StreamedAmbience> Active = new();

    // ---- 诊断计数（只增不减，供调试面板读取）----
    public static long TotalUnderruns;
    public static long TotalSlotRejections;
    public static long TotalWorkerJoinTimeouts;

    private readonly Mod _mod;
    private readonly Func<StreamedAmbience, bool> _playWhen;
    private readonly AutoResetEvent _wake = new(false);

    private string _path;
    private float _volume;
    private float _maxVolume = 1f;
    private bool _disposed;
    private bool _failed;
    private bool _started;

    // 主线程状态
    private DynamicSoundEffectInstance _instance;
    private Thread _worker;

    // 第二层兜底：流失败后在同一条音轨上退化为常驻 SoundEffect，
    // 公开面不变，字段与音量梯度都不必改。只在真的坏了之后才付这份内存。
    private SoundEffectInstance _fallback;
    private bool _fallbackTried;

    // 后台线程状态（经 _readyForPlayback / _workerFailed 的释放语义发布给主线程）
    private BlockQueue _queue;
    private int _blockFrames;
    private int _blockBytes;
    private int _sampleRate;
    private AudioChannels _outChannels;

    // 每个工作线程持有独立令牌。共享一个 bool 的话，旧线程 Join 超时未退出时
    // 会被新线程把标志改回 false，导致两个线程同时往一个队列写。
    private sealed class WorkerToken {
        public volatile bool Stop;
    }

    private WorkerToken _token;
    private volatile bool _readyForPlayback;
    private volatile bool _workerFailed;
    private Exception _workerError;

    public StreamedAmbience(Mod mod, string path, string name, float maxVolume, float volumeStep,
                            Func<StreamedAmbience, bool> playWhen) {
        _mod = mod;
        _path = path;
        Name = name;
        _maxVolume = maxVolume;
        VolumeStep = volumeStep;
        _playWhen = playWhen;
    }

    public string Name { get; }

    /// <summary>当前淡入淡出音量（0–1），不含 <c>Main.ambientVolume</c>。</summary>
    public float Volume => _volume;

    /// <summary>目标音量。上层每帧按梯度重算，不做上限裁剪，与 <c>ModAmbience</c> 一致。</summary>
    public float MaxVolume {
        get => _maxVolume;
        set => _maxVolume = value;
    }

    public float VolumeStep { get; set; } = 0.0075f;

    public bool AreConditionsMet { get; private set; }

    public bool PlayInMenu { get; set; }

    /// <summary>变调量，每帧由上层写入，播放时应用到设备。</summary>
    public float Pitch { get; set; }

    public bool IsPlaying => _instance is not null || _fallback is not null;

    public bool Failed => _failed;

    /// <summary>是否已退化到常驻播放（流失败后的第二层兜底）。</summary>
    public bool FallbackActive { get; private set; }

    /// <summary>因槽位不足被拒绝启动的次数。</summary>
    public long SlotRejections { get; private set; }

    public Exception LastError { get; private set; }

    public long Underruns { get; private set; }

    public int SeamFrames { get; private set; }

    public string AssetPath => _path;

    /// <summary>供调试面板解引用；未播放时为 null。</summary>
    public SoundEffectInstance SoundInstance => _instance;

    public string Describe() =>
        $"流 {Name}: vol={_volume:0.###} max={_maxVolume:0.###} " +
        $"{(IsPlaying ? "playing" : _failed ? "failed" : "idle")} " +
        $"sq={_queue?.ReadyCount ?? 0}/{_queue?.FreeCount ?? 0} under={Underruns} " +
        $"seam={SeamFrames} reject={SlotRejections}";

    /// <summary>切换曲目。会停掉当前流，下一次 Update 按新路径重开。</summary>
    public void ChangeTrack(string path) {
        if (Main.dedServ) return;
        if (_path == path && !_failed) return;

        StopPlayback();
        StopFallback();
        _path = path;
        _failed = false;
        _fallbackTried = false;
        LastError = null;
        Underruns = 0;
    }

    /// <summary>每帧由主线程调用一次：推进音量包络，并按状态推进打开/播放/停机。</summary>
    public void Update() {
        if (Main.dedServ || _disposed) return;

        ApplyVolumeEnvelope();

        // 全局关闭流式，或本条已经失败：按需加载常驻副本，绝不启动解码线程。
        if (ForceResident || _failed) {
            if (_fallback is null && _volume > 0f)
                TryResidentFallback();
            return;
        }

        if (_workerFailed) {
            Fail(_workerError ?? new InvalidOperationException("解码线程异常退出。"));
            return;
        }

        if (_instance is null) {
            if (_volume > 0f) EnsureWorker();
            if (_readyForPlayback && _worker is not null)
                StartPlayback();
            else if (_volume <= 0f && _worker is not null)
                StopPlayback();   // 还没起播就淡出了，别把解码线程挂着
        }
        else {
            Pump();
            if (_volume <= 0f) StopPlayback();
        }
    }

    /// <summary>复制 API 版 <c>INTERNAL_Update</c> 的包络语义，保持听感一致。</summary>
    private void ApplyVolumeEnvelope() {
        if (!PlayInMenu && Main.gameMenu) {
            _volume = 0f;
            ApplyInstanceState();
            return;
        }

        AreConditionsMet = _playWhen?.Invoke(this) ?? false;

        bool focused = Main.hasFocus;
        bool fadeOnLost = true;
        try {
            var cfg = ModContent.GetInstance<PlayerConfig>();
            if (cfg is not null) fadeOnLost = cfg.player_FadeAmbienceOnFocusLost;
        }
        catch {
            // 配置尚未注册时按「淡出」处理，与默认值一致。
        }

        if (AreConditionsMet && focused) {
            _volume += VolumeStep;
            if (_volume > _maxVolume) _volume = _maxVolume;
        }
        if (!AreConditionsMet || (!focused && fadeOnLost) || Main.gameMenu) {
            _volume -= VolumeStep;
        }

        if (_volume < 0f) _volume = 0f;
        else if (_volume > 1f) _volume = 1f;

        ApplyInstanceState();
    }

    private void ApplyInstanceState() {
        SoundEffectInstance target = _instance ?? _fallback;
        if (target is null) return;
        try {
            target.Volume = Math.Clamp(_volume * Main.ambientVolume, 0f, 1f);
            target.Pitch = Pitch;
        }
        catch {
            // 设备回收期间设置属性会抛异常，下一帧会自行恢复或走失败路径。
        }
    }

    /// <summary>主线程：把队列里已解码的块交给设备。</summary>
    private void Pump() {
        var inst = _instance;
        var queue = _queue;
        if (inst is null || queue is null) return;

        if (_started && inst.PendingBufferCount == 0) {
            Underruns++;
            TotalUnderruns++;
        }

        int guard = 0;
        while (inst.PendingBufferCount < TargetPendingBuffers && guard++ < QueueCapacity) {
            byte[] block = queue.Take();
            if (block is null) break;

            try {
                inst.SubmitBuffer(block, 0, _blockBytes);
            }
            catch (Exception e) {
                queue.Return(block);
                Fail(e);
                return;
            }

            queue.Return(block);
        }
    }

    private void EnsureWorker() {
        if (_worker is not null) return;

        if (Active.Count >= MaxActiveStreams) {
            SlotRejections++;
            TotalSlotRejections++;
            return;
        }

        var token = new WorkerToken();
        _token = token;
        _workerFailed = false;
        _workerError = null;
        _readyForPlayback = false;

        Active.Add(this);
        _worker = new Thread(() => DecodeLoop(token)) {
            IsBackground = true,
            Name = $"TerrariaAmbience.Stream.{Name}",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    private void DecodeLoop(WorkerToken token) {
        OggStream stream = null;
        try {
            Stream raw = OpenAssetStream(_path);
            if (raw is null)
                throw new FileNotFoundException($"找不到音频资源：{_path}.ogg");

            stream = OggStream.OpenFromStream(raw, OggStream.DefaultCrossfadeFrames);

            int frames = Math.Max(256, (int)Math.Round(stream.SampleRate * BlockSeconds));
            int bytes = frames * stream.Channels * 2;
            var queue = new BlockQueue(QueueCapacity, bytes);

            // 预填必须在主线程 Play 之前完成：Play 之前 State 为 Stopped，
            // SubmitBuffer 只会记账，Play 时才真正提交，这样起播不会空转。
            for (int i = 0; i < TargetPendingBuffers; i++) {
                byte[] block = queue.Rent();
                stream.ReadPcm16(block, frames);
                if (stream.Faulted)
                    throw new IOException("预填阶段解码失败。");
                queue.Commit(block);
            }

            // 已被要求停止的线程不得发布状态，否则可能覆盖新一代线程的队列。
            if (token.Stop) return;

            _queue = queue;
            _blockFrames = frames;
            _blockBytes = bytes;
            _sampleRate = stream.SampleRate;
            _outChannels = stream.Channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo;
            SeamFrames = stream.SeamFrames;
            _readyForPlayback = true; // 释放屏障：以上字段对主线程可见

            while (!token.Stop) {
                byte[] block = queue.Rent();
                if (block is null) {
                    _wake.WaitOne(8);
                    continue;
                }

                if (token.Stop) {
                    queue.Return(block);
                    break;
                }

                stream.ReadPcm16(block, frames);
                if (stream.Faulted)
                    throw new IOException("循环解码失败。");

                queue.Commit(block);
            }
        }
        catch (Exception e) {
            _workerError = e;
            _readyForPlayback = false;
            _workerFailed = true;
        }
        finally {
            try { stream?.Dispose(); } catch { }
        }
    }

    private void StartPlayback() {
        try {
            var inst = new DynamicSoundEffectInstance(_sampleRate, _outChannels);
            _instance = inst;

            Pump();          // State == Stopped：只记账
            inst.Play();     // QueueInitialBuffers 真正提交
            _started = true;
            ApplyInstanceState();
        }
        catch (Exception e) {
            _instance = null;
            _started = false;
            Fail(e);
        }
    }

    private void StopPlayback() {
        var inst = _instance;
        _instance = null;
        _started = false;

        if (inst is not null) {
            try { inst.Stop(); } catch { }
            try { inst.Dispose(); } catch { }
        }

        StopWorker();
        Active.Remove(this);
    }

    private void StopWorker() {
        var token = _token;
        _token = null;
        if (token is not null) token.Stop = true;
        _wake.Set();

        var worker = _worker;
        _worker = null;

        if (worker is not null && worker != Thread.CurrentThread) {
            if (!worker.Join(WorkerJoinTimeoutMs)) {
                // 不阻塞主线程。旧线程持有自己的令牌与队列，会自行退出，不会污染新线程。
                TotalWorkerJoinTimeouts++;
                return;
            }
        }

        _queue = null;
        _readyForPlayback = false;
    }

    private void Fail(Exception e) {
        if (_failed) return;
        _failed = true;
        LastError = e;
        try { StopPlayback(); } catch { }
        // 兜底实例延迟到「真的要出声」时再加载，避免启动瞬间把 25 条全解压一遍。
    }

    /// <summary>
    /// 第二层兜底：把这条音轨退化成常驻 SoundEffect，保证「宁可多占几 MB，也不能没声音」。
    /// 只在失败后执行一次；再失败就保持静音，不递归。
    /// </summary>
    private void TryResidentFallback() {
        if (_fallbackTried || Main.dedServ) return;
        _fallbackTried = true;

        try {
            var asset = _mod.Assets.Request<SoundEffect>(_path, ReLogic.Content.AssetRequestMode.ImmediateLoad);
            var inst = asset?.Value?.CreateInstance();
            if (inst is null) return;

            inst.IsLooped = true;
            inst.Volume = Math.Clamp(_volume * Main.ambientVolume, 0f, 1f);
            inst.Play();

            _fallback = inst;
            FallbackActive = true;
        }
        catch (Exception e) {
            LastError = e;
        }
    }

    private Stream OpenAssetStream(string path) {
        string name = path + ".ogg";

        try {
            // newFileStream: true → 独立的 FileStream + EntryReadStream，
            // 不与 tML 的共享读取流互斥，也不会把整条音轨读进内存。
            Stream s = _mod.GetFileStream(name, newFileStream: true);
            if (s is not null) return s;
        }
        catch {
            // 回落到源码目录（开发环境 / 未打包场景）。
        }

        string folder = _mod.SourceFolder;
        if (!string.IsNullOrEmpty(folder)) {
            string full = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return File.OpenRead(full);
        }

        return null;
    }

    private void StopFallback() {
        var fb = _fallback;
        _fallback = null;
        FallbackActive = false;
        if (fb is null) return;

        try { fb.Stop(); } catch { }
        try { fb.Dispose(); } catch { }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { StopPlayback(); } catch { }
        try { StopFallback(); } catch { }
        Active.Remove(this);
        // 不释放 _wake：若存在 Join 超时的残留线程，释放会让它抛 ObjectDisposedException。
        // 一个内核句柄交给终结器即可。
    }

    /// <summary>卸载模组时兜底：把所有活着的流停掉并释放设备句柄。</summary>
    public static void DisposeAll() {
        foreach (var amb in Active.ToArray()) {
            try { amb.Dispose(); } catch { }
        }
        Active.Clear();
    }
}
