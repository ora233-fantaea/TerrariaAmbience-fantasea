using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using System;
using System.Runtime.CompilerServices;

namespace TerrariaAmbience.Sounds.SoundFilters.FAudioHacks;

public class FAudioReverbController {

    public static readonly FAudio.FAudioFXReverbParameters DefaultFAudioReverb = new() {
        WetDryMix = 100f,
        ReflectionsDelay = 7,
        ReverbDelay = 11,
        RearDelay = FAudio.FAUDIOFX_REVERB_DEFAULT_REAR_DELAY,
        PositionLeft = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION,
        PositionRight = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION,
        PositionMatrixLeft = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION_MATRIX,
        PositionMatrixRight = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION_MATRIX,
        EarlyDiffusion = FAudio.FAUDIOFX_REVERB_DEFAULT_EARLY_DIFFUSION, // 15?
        LateDiffusion = FAudio.FAUDIOFX_REVERB_DEFAULT_LATE_DIFFUSION, // also 15?
        LowEQGain = FAudio.FAUDIOFX_REVERB_DEFAULT_LOW_EQ_GAIN,
        LowEQCutoff = FAudio.FAUDIOFX_REVERB_DEFAULT_LOW_EQ_CUTOFF,
        HighEQGain = FAudio.FAUDIOFX_REVERB_DEFAULT_HIGH_EQ_GAIN,
        HighEQCutoff = FAudio.FAUDIOFX_REVERB_DEFAULT_HIGH_EQ_CUTOFF, // 6?
        RoomFilterFreq = FAudio.FAUDIOFX_REVERB_DEFAULT_ROOM_FILTER_FREQ,
        RoomFilterMain = FAudio.FAUDIOFX_REVERB_DEFAULT_ROOM_FILTER_MAIN, // -10?
        RoomFilterHF = FAudio.FAUDIOFX_REVERB_DEFAULT_ROOM_FILTER_HF, // -1?
        ReflectionsGain = FAudio.FAUDIOFX_REVERB_DEFAULT_REFLECTIONS_GAIN, // -26.02?
        ReverbGain = 10.0f, // FAudio.FAUDIOFX_REVERB_DEFAULT_REVERB_GAIN,
        DecayTime = 1.49000001f,
        Density = FAudio.FAUDIOFX_REVERB_DEFAULT_DENSITY,
        RoomSize = FAudio.FAUDIOFX_REVERB_DEFAULT_ROOM_SIZE
    };

    /// <summary>发送目标只在 <see cref="RvInit"/> 里建一次：主输出 + 混响子混音。</summary>
    static bool _init;
    static FAudio.FAudioVoiceSends _reverbSends;
    static IntPtr _reverbSendPtr;

    /// <summary>
    /// 每个音源上一次成功挂上混响时的 voice handle。
    /// FNA 在 <c>Stop()</c> 之后再 <c>Play()</c> 会重建 source voice（handle 变化），
    /// 此时 <c>usingReverb</c> 仍是 true，但新 voice 根本没有混响发送 —— 必须按 handle 重挂。
    /// 用弱键表，音源被回收后条目自动消失，不需要额外清理。
    /// </summary>
    static readonly ConditionalWeakTable<SoundEffectInstance, StrongBox<nint>> _attached = new();

    public static bool IsInitialized => _init;

    public unsafe static void ApplyCustomReverb(SoundEffectInstance inst, float rvGain, FilterParams param) {
        var handle = inst.handle;

        if (handle == IntPtr.Zero) {
            return;
        }

        bool attachedToThisVoice = inst.usingReverb
            && _attached.TryGetValue(inst, out var box)
            && box.Value == handle;

        if (!attachedToThisVoice) {
            // 全局关掉混响时，连发送都不挂：不碰 device.ReverbVoice 的共享参数，
            // 也不为这条音源改路由。真正「关了就是关了」。
            if (!param.ReverbEnabled) {
                return;
            }

            ReverbAttach(handle, param.Reverb);
            inst.usingReverb = true;
            _attached.AddOrUpdate(inst, new StrongBox<nint>(handle));
        }
        // 已挂在当前 voice 上：房间参数由 UpdateRoomParams 在刷新时统一推送，
        // 这里只维护这一条音源的发送量。

        SetSendGain(handle, inst, param.ReverbEnabled ? rvGain : 0f);
    }

    /// <summary>
    /// 只写「源 → 混响」这一路的发送矩阵。
    /// 关键：不能用 <c>inst.dspSettings.pMatrixCoefficients</c> —— 那块缓冲同时是 FNA
    /// 推给主输出的干路矩阵，写进去之后任何一次 <c>Play()</c>/<c>Pan</c> 复用都会把干路音量改成 rvGain。
    /// 这里改用栈上的局部数组，干路完全不受影响。
    /// </summary>
    static unsafe void SetSendGain(IntPtr handle, SoundEffectInstance inst, float gain) {
        int srcChannels = (int)inst.dspSettings.SrcChannelCount;

        if (srcChannels <= 0) {
            return;
        }

        float* send = stackalloc float[srcChannels];
        for (int i = 0; i < srcChannels; i++) {
            send[i] = gain;
        }

        FAudio.FAudioVoice_SetOutputMatrix(handle, SoundEffect.Device().ReverbVoice,
            (uint)srcChannels, 1u, (IntPtr)send, 0);
    }

    /// <summary>
    /// 把「当前房间」的混响参数写到共享的混响子混音上。
    /// 原来只在某个音源首次挂载时写一次，结果是尾音长度取决于「最后一个新开的音效」所在的房间，
    /// 而不是玩家当前所在的房间 —— 换房间后听感不跟着变。
    /// </summary>
    public unsafe static void UpdateRoomParams(FAudio.FAudioFXReverbParameters param) {
        if (!_init) {
            return;
        }

        var device = SoundEffect.Device();
        var rv = param;

        FAudio.FAudioVoice_SetEffectParameters(device.ReverbVoice, 0, (IntPtr)(&rv),
            (uint)MarshalHelper.SizeOf<FAudio.FAudioFXReverbParameters>(), 0);
    }

    // yadda yadda... create more instances somehow. too lazy, too bad.
    public unsafe static void RvInit() {
        if (_init)
            return;

        _init = true;

        var device = SoundEffect.Device();

        FAudio.FAudioCreateReverb(out nint reverb, 0);

        IntPtr chainPtr;
        chainPtr = FNAPlatform.Malloc(MarshalHelper.SizeOf<FAudio.FAudioEffectChain>());

        FAudio.FAudioEffectChain* reverbChain = (FAudio.FAudioEffectChain*)chainPtr;

        reverbChain->EffectCount = 1;
        reverbChain->pEffectDescriptors = FNAPlatform.Malloc(MarshalHelper.SizeOf<FAudio.FAudioEffectDescriptor>());

        FAudio.FAudioEffectDescriptor* reverbDesc = (FAudio.FAudioEffectDescriptor*)reverbChain->pEffectDescriptors;

        reverbDesc->InitialState = 1;
        reverbDesc->OutputChannels = (uint)(
            (device.DeviceDetails.OutputFormat.Format.nChannels == 6) ? 6 : 1
        );
        reverbDesc->pEffect = reverb;

        FAudio.FAudio_CreateSubmixVoice(device.Handle, out device.ReverbVoice, 1,
            // always omnidirectional reverb
            device.DeviceDetails.OutputFormat.Format.nSamplesPerSec, 0, 0, IntPtr.Zero, chainPtr);

        FAudio.FAPOBase_Release(reverb);

        FNAPlatform.Free(reverbChain->pEffectDescriptors);
        FNAPlatform.Free(chainPtr);

        // 所有音源的发送目标都是「主输出 + 混响」这一对，只分配一次并复用。
        // 原实现在每个音源首次挂混响时都 Malloc 一次且永不释放。
        _reverbSendPtr = FNAPlatform.Malloc(2 * MarshalHelper.SizeOf<FAudio.FAudioSendDescriptor>());
        _reverbSends.SendCount = 2;
        _reverbSends.pSends = _reverbSendPtr;

        FAudio.FAudioSendDescriptor* sendDesc = (FAudio.FAudioSendDescriptor*)_reverbSendPtr;
        sendDesc[0].Flags = 0;
        sendDesc[0].pOutputVoice = device.MasterVoice;
        sendDesc[1].Flags = 0;
        sendDesc[1].pOutputVoice = device.ReverbVoice;
    }

    /// <summary>释放本类持有的原生内存。必须在所有音源已 Dispose 之后调用。</summary>
    public static void Shutdown() {
        if (_reverbSendPtr != IntPtr.Zero) {
            FNAPlatform.Free(_reverbSendPtr);
            _reverbSendPtr = IntPtr.Zero;
        }

        _reverbSends = default;
        _attached.Clear();
        // 不主动销毁 ReverbVoice：它由 FAudio 设备持有，FNA 关闭设备时会一并释放；
        // 在仍有音源向它发送时提前销毁会踩空指针。标志重置后下次加载会重建。
        _init = false;
    }

    unsafe static void ReverbAttach(IntPtr handle, FAudio.FAudioFXReverbParameters param) {
        UpdateRoomParams(param);

        // 复用 RvInit 里建好的发送描述符，不再逐音源分配。
        FAudio.FAudioVoice_SetOutputVoices(handle, ref _reverbSends);
    }
}
