using System;
using System.Collections.Generic;

namespace TerrariaAmbience.Sounds.Streaming;

/// <summary>
/// 定长 PCM 块的环形交换区：解码线程「租一块 → 写满 → 提交」，
/// 主线程「取一块 → SubmitBuffer → 归还」。
///
/// 之所以可以归还后立刻复用，是因为 FNA 的 SubmitBuffer 会把数据 Marshal.Copy 进原生内存，
/// 不持有托管数组的引用。这样整条链路只有固定几块缓冲，不会产生大对象堆碎片。
/// </summary>
internal sealed class BlockQueue {
    private readonly byte[][] _blocks;
    private readonly Queue<int> _free = new();
    private readonly Queue<int> _ready = new();
    private readonly object _lock = new();

    public BlockQueue(int capacity, int blockBytes) {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (blockBytes <= 0) throw new ArgumentOutOfRangeException(nameof(blockBytes));

        Capacity = capacity;
        BlockBytes = blockBytes;
        _blocks = new byte[capacity][];
        for (int i = 0; i < capacity; i++) {
            _blocks[i] = new byte[blockBytes];
            _free.Enqueue(i);
        }
    }

    public int Capacity { get; }
    public int BlockBytes { get; }

    public int FreeCount {
        get { lock (_lock) return _free.Count; }
    }

    public int ReadyCount {
        get { lock (_lock) return _ready.Count; }
    }

    /// <summary>租一块空闲缓冲；没有空闲块时返回 null。</summary>
    public byte[] Rent() {
        lock (_lock) {
            if (_free.Count == 0) return null;
            return _blocks[_free.Dequeue()];
        }
    }

    /// <summary>解码完成后标记为可被主线程取走。</summary>
    public void Commit(byte[] block) {
        int index = IndexOf(block);
        if (index < 0) throw new ArgumentException("该缓冲区不属于本队列。", nameof(block));
        lock (_lock) _ready.Enqueue(index);
    }

    /// <summary>取一块已解码的缓冲；没有时返回 null。</summary>
    public byte[] Take() {
        lock (_lock) {
            if (_ready.Count == 0) return null;
            return _blocks[_ready.Dequeue()];
        }
    }

    /// <summary>提交给设备后归还，供解码线程复用。</summary>
    public void Return(byte[] block) {
        int index = IndexOf(block);
        if (index < 0) throw new ArgumentException("该缓冲区不属于本队列。", nameof(block));
        lock (_lock) _free.Enqueue(index);
    }

    private int IndexOf(byte[] block) => Array.IndexOf(_blocks, block);
}
