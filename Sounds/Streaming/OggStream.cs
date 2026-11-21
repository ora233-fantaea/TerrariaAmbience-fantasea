using System;
using System.IO;
using NVorbis;

namespace TerrariaAmbience.Sounds.Streaming;

/// <summary>
/// 单条 OGG 的解码内核。只负责「按帧取 PCM」，不接触音频设备，也不做线程调度，
/// 因此可以脱离 tModLoader 单独编译与离线回归。
///
/// 循环采用「接缝合成」：正常区播到 total-W 时，顺序解出尾部 W 帧（不输出），
/// 与打开时缓存的头部 W 帧按等功率曲线混合，作为接缝输出，然后跳回第 W 帧。
///
/// 之所以不在播放中 seek 到尾部取材：NVorbis 的 SamplePosition 在距文件末尾
/// 约 4096–16384 帧以内会定位错误（实测偏差可达满量程的 1/3），只能顺序解码；
/// 距末尾 16384 帧以外的位置 seek 是精确的（逐样本 0 误差），所以跳回第 W 帧安全。
/// </summary>
public sealed class OggStream : IDisposable {
    /// <summary>默认交叉淡化长度（帧）。在 44.1 kHz 下约 5.8 ms。</summary>
    public const int DefaultCrossfadeFrames = 256;

    private readonly Stream _source;
    private readonly VorbisReader _reader;

    private readonly int _seamFrames;
    private readonly long _loopFrames;

    /// <summary>头部 W 帧（打开时缓存）。接缝的淡入侧，可为 null。</summary>
    private readonly float[] _headFloat;

    /// <summary>尾部 W 帧（循环点顺序解码）。接缝的淡出侧，可为 null。</summary>
    private readonly float[] _tailFloat;

    /// <summary>合成后的接缝，每圈重算一次，可为 null。</summary>
    private readonly short[] _seamPcm;

    /// <summary>单次读取的浮点暂存，按需增长。</summary>
    private float[] _scratch;

    private long _playPos;
    private int _seamPos;
    private bool _inSeam;
    private bool _faulted;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalFrames { get; }

    /// <summary>不含接缝的循环长度。接缝之后从这里继续。</summary>
    public long LoopFrames => _loopFrames;

    /// <summary>接缝长度（帧）。0 表示未做交叉淡化。</summary>
    public int SeamFrames => _seamFrames;

    /// <summary>解码过程中是否出现过不可恢复的错误（已用静音补齐）。</summary>
    public bool Faulted => _faulted;

    private OggStream(Stream source, VorbisReader reader, int requestedCrossfade) {
        _source = source;
        _reader = reader;

        SampleRate = reader.SampleRate;
        Channels = Math.Max(1, reader.Channels);
        TotalFrames = Math.Max(0L, reader.TotalSamples);

        if (SampleRate <= 0)
            throw new InvalidDataException("OGG 报告了非法的采样率。");
        if (Channels > 2)
            throw new NotSupportedException($"暂不支持 {Channels} 声道，仅支持单声道与立体声。");
        if (TotalFrames <= 0)
            throw new InvalidDataException("OGG 未报告有效的样本总数。");

        _scratch = new float[1024 * Channels];

        int seam = ChooseSeamFrames(TotalFrames, requestedCrossfade);
        float[] head = null, tail = null;
        short[] seamPcm = null;

        if (seam > 0) {
            int count = seam * Channels;
            head = new float[count];
            tail = new float[count];
            seamPcm = new short[count];

            // 打开时解码器正停在 0，先取头部；读取失败就退化成「不做淡化」。
            int got = ReadSamplesFully(head, count);
            if (got < count) {
                head = tail = null;
                seamPcm = null;
                seam = 0;
            }
            else if (!SeekToFrame(0)) {
                head = tail = null;
                seamPcm = null;
                seam = 0;
            }
        }

        _seamFrames = seam;
        _loopFrames = TotalFrames - seam;
        _headFloat = head;
        _tailFloat = tail;
        _seamPcm = seamPcm;
    }

    /// <summary>从一个可寻址的流打开。流的所有权移交本对象，Dispose 时一并关闭。</summary>
    public static OggStream OpenFromStream(Stream source, int crossfadeFrames = DefaultCrossfadeFrames) {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!source.CanRead) throw new ArgumentException("流不可读。", nameof(source));

        VorbisReader reader = null;
        try {
            reader = new VorbisReader(source, closeOnDispose: true);
            return new OggStream(source, reader, crossfadeFrames);
        }
        catch {
            try { reader?.Dispose(); } catch { }
            try { source.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 读取指定帧数并写成 16 位交错 PCM。返回的永远是 frameCount（出错时余下部分为静音），
    /// 这样调用方的缓冲区尺寸恒定，可以直接交给音频设备。
    /// </summary>
    public int ReadPcm16(byte[] dst, int frameCount) {
        if (dst is null) throw new ArgumentNullException(nameof(dst));
        if (frameCount <= 0) return 0;

        int ch = Channels;
        int frameBytes = ch * 2;
        int needed = frameCount * frameBytes;
        if (dst.Length < needed)
            throw new ArgumentException($"缓冲区过小：需要 {needed} 字节，实际 {dst.Length}。", nameof(dst));

        int produced = 0;

        while (produced < frameCount) {
            if (_inSeam) {
                int take = Math.Min(frameCount - produced, _seamFrames - _seamPos);
                if (take > 0) {
                    int src = _seamPos * ch;
                    int dstSample = produced * ch;
                    for (int i = 0; i < take * ch; i++) {
                        short s = _seamPcm[src + i];
                        int o = (dstSample + i) * 2;
                        dst[o] = (byte)(s & 0xFF);
                        dst[o + 1] = (byte)((s >> 8) & 0xFF);
                    }

                    _seamPos += take;
                    produced += take;
                }

                if (_seamPos >= _seamFrames) {
                    _inSeam = false;
                    _seamPos = 0;
                    // 只 seek 到 0（唯一被证明精确的位置），再顺序解码丢弃 W 帧。
                    // 若直接 seek 到 W，遇到 granule 元数据损坏的文件会落到错误位置。
                    if (!SeekToFrame(0)) break;
                    if (!SkipFrames(_seamFrames)) {
                        _faulted = true;
                        break;
                    }
                    _playPos = _seamFrames;
                }
                continue;
            }

            long remain = _loopFrames - _playPos;
            if (remain <= 0) {
                if (_seamFrames > 0) {
                    if (!PrepareSeam()) {
                        _faulted = true;
                        break;
                    }
                    _inSeam = true;
                    _seamPos = 0;
                    continue;
                }

                if (!SeekToFrame(0)) break;
                _playPos = 0;
                continue;
            }

            int want = (int)Math.Min(frameCount - produced, remain);
            int got = ReadFramesInto(dst, produced, want);
            if (got <= 0) {
                _faulted = true;
                break;
            }

            produced += got;
            _playPos += got;
        }

        if (produced < frameCount) {
            Array.Clear(dst, produced * frameBytes, (frameCount - produced) * frameBytes);
            return frameCount;
        }
        return produced;
    }

    /// <summary>
    /// 在循环点合成接缝。此时解码器正好停在 total-W，顺序读 W 帧即为尾部，
    /// 与打开时缓存的头部做等功率混合。全程不用 seek。
    /// </summary>
    private bool PrepareSeam() {
        int ch = Channels;
        int w = _seamFrames;
        int count = w * ch;

        int got = ReadSamplesFully(_tailFloat, count);
        if (got <= 0) return false;
        int tailFrames = got / ch;

        double denom = w + 1.0;
        for (int i = 0; i < w; i++) {
            double t = (i + 1) / denom;
            float gainIn = (float)Math.Sin(t * Math.PI / 2.0);
            float gainOut = (float)Math.Cos(t * Math.PI / 2.0);
            int row = i * ch;
            bool hasTail = i < tailFrames;

            for (int c = 0; c < ch; c++) {
                int k = row + c;
                float tail = hasTail ? _tailFloat[k] : 0f;
                _seamPcm[k] = ToShort(tail * gainOut + _headFloat[k] * gainIn);
            }
        }

        return true;
    }

    /// <summary>
    /// 顺序解码并丢弃若干帧。用于接缝之后「跳回第 W 帧」：
    /// 等价于 seek 到 W，但只依赖从 0 开始的顺序读取，不受 granule 元数据损坏影响。
    /// </summary>
    private bool SkipFrames(int frames) {
        if (frames <= 0) return true;

        int need = frames * Channels;
        if (_scratch.Length < need)
            _scratch = new float[need];

        return ReadSamplesFully(_scratch, need) == need;
    }

    private int ReadFramesInto(byte[] dst, int dstFrame, int wantFrames) {
        int ch = Channels;
        int need = wantFrames * ch;
        if (_scratch.Length < need)
            _scratch = new float[need];

        int got = ReadSamplesFully(_scratch, need);
        int frames = got / ch;

        int dstSample = dstFrame * ch;
        int count = frames * ch;
        for (int i = 0; i < count; i++) {
            short s = ToShort(_scratch[i]);
            int o = (dstSample + i) * 2;
            dst[o] = (byte)(s & 0xFF);
            dst[o + 1] = (byte)((s >> 8) & 0xFF);
        }

        return frames;
    }

    private int ReadSamplesFully(float[] buf, int count) {
        int total = 0;
        while (total < count) {
            int want = count - total;
            want -= want % Channels;
            if (want <= 0) break;

            int n;
            try {
                n = _reader.ReadSamples(buf, total, want);
            }
            catch {
                _faulted = true;
                return total;
            }
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private bool SeekToFrame(long frame) {
        try {
            if (frame < 0) frame = 0;
            if (TotalFrames > 0 && frame >= TotalFrames) frame = TotalFrames - 1;
            _reader.SamplePosition = frame;
            return true;
        }
        catch {
            _faulted = true;
            return false;
        }
    }

    private static int ChooseSeamFrames(long total, int requested) {
        if (requested <= 0) return 0;
        int w = requested;
        // 太短的音轨按四分之一取，避免接缝把整条曲子吃掉。
        if (total < (long)w * 4) w = (int)(total / 4);
        return w >= 8 ? w : 0;
    }

    private static short ToShort(float value) {
        if (value > 1f) value = 1f;
        else if (value < -1f) value = -1f;
        return (short)MathF.Round(value * 32767f);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        try { _reader?.Dispose(); } catch { }
        // VorbisReader 在 closeOnDispose 时已关闭源流；这里再兜一次，重复 Dispose 无害。
        try { _source?.Dispose(); } catch { }
    }
}
