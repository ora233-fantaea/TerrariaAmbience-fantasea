# 流式可行性验证（2026-11-21）

> 分支：`stream-1.4`（基于 `1.4` @ `66d0e73`）
> 结论：**可行**。方案里有 1 条 API 假设是错的、1 条设计是多余的、1 条数字偏低；
> 解码与容器访问已在**真实 tModLoader 进程**里跑通，接缝问题已量化并给出可验证的修法。
> 本文只验证「怎么做」，不含接入改动（落地按约定暂缓）。

---

## 0. 结论先行

| 问题 | 答案 | 依据 |
|---|---|---|
| NVorbis 能不能在模组里用 | **能**，不需要自带 DLL | 编译 + 服务器实机日志（§2.4） |
| `BufferNeeded` 会不会被泵 | **会**，每 tick 一次，最多补 3 块 | FNA 与 `Main.Update` 反编译（§2.1） |
| 单声道要不要上混 | **不要**，按文件声道数建实例 | NVorbis 无声道参数重载（§2.5） |
| 循环能不能直接回绕 | **不够**，39 条里 10 条接缝超标 | 离线接缝统计（§2.8） |
| 解码放主线程够不够 | 够但会偶发吃半帧，**建议工作线程** | 实测 P95 1.0 ms / max 9 ms（§2.9） |
| 内存收益 | biome 369.2 MB → ≤4 条流 ×（≤2.2 MB 压缩 + 0.24 MB PCM） | 实测（§2.7、§3.3） |

一句话：**用「工作线程解码 + 主线程提交 + 3×100 ms 在飞 + 回绕填充 + 256 帧等功率交叉淡化」**，
其余（混响、变调、音量包络、ChangeTrack）都能零改动复用现有链路。

---

## 1. 验证方法与可复现路径

| 环节 | 工具 | 说明 |
|---|---|---|
| API 事实 | `ilspycmd` 反编译 FNA / tModLoader | 结论全部给出行号级出处 |
| 调用点 | 自写 IL 扫描器 `.home/qa/stream-verify-tool` | 按 `call`/`callvirt` 的操作数 token 反解目标方法 |
| 解码/接缝 | `.home/qa/stream-harness`（`oggprobe`） | 用与生产同构的填充逻辑跑 2.2 圈，统计 Δ 分布 |
| 运行时 | `.home/qa/probe-smoke` + `StreamProbe` 探针模组 | 真实 tML 服务器进程里解码立体声与单声道各一条 |
| 编译面 | 在隔离副本 (`iso/ModSources`) 编译骨架 | 0 error，7 条既有警告不变 |

复现：

```bash
# 调用点扫描：谁在泵 FrameworkDispatcher
dotnet .home/qa/stream-verify-tool/bin/Release/net8.0/probe.dll \
  "D:/SteamLibrary/steamapps/common/tModLoader/tModLoader.dll" calls FrameworkDispatcher Update

# 单条音轨解码 + 接缝
dotnet .home/qa/stream-harness/bin/Release/net8.0/oggprobe.dll \
  "Sounds/Custom/ambient/biome/snow/windy.ogg" - 2.2

# 交叉淡化对比
dotnet .home/qa/stream-harness/bin/Release/net8.0/oggprobe.dll --xfade \
  "Sounds/Custom/ambient/biome/snow/windy.ogg" 64 256 441

# 真实进程探针（服务端，无音频设备也能跑解码）
python .home/qa/probe_run.py
grep PROBE .home/qa/iso/tModLoader-Logs/server.log
```

环境：tModLoader `2026.9.3.1` / FNA `23.10.0.0` / NVorbis `0.10.0.3` / .NET 8。
`FNA.dll` 由 `Krafs.Publicizer` 公开化，因此 `handle` / `dspSettings` / `usingReverb` 可直接访问。

---

## 2. 逐条事实核查

### 2.1 `BufferNeeded` 会被泵送 ✅（但方案漏了时序）

证据链：

1. `FNA/FrameworkDispatcher.Update()` 遍历 `Streams`，逐个调 `DynamicSoundEffectInstance.Update()`。
2. `FNA/Game.Update()` 调 `FrameworkDispatcher.Update()` —— 全程序集只有这一处调用点（扫描 hits=1）。
3. `Terraria.Main.Update` 末尾显式调基类：`((Game)this).Update(gameTime);`
   （`main-decomp.cs:16791`，注意是强转后调用，不是 `base.Update`，按字面搜 `base.` 会漏）。
4. `DynamicSoundEffectInstance.Update()`：`State != Playing` 直接返回；否则按 `3 - PendingBufferCount`
   触发 `BufferNeeded`。

**三条推论（都会影响实现）**：

- 首次预填必须在 `Play()` **之前** 完成。`Play()` 内部的 `QueueInitialBuffers()` 只提交一次，
  而 `Play()` 自己调用的 `Update()` 在 `State==Stopped` 时提前返回，不会触发回调。
- 泵发生在 `DoUpdate` 之后；本模组的 `On_Main.Update` 钩子又是 `orig` 之后执行，
  所以我们的补货比泵晚一 tick（16.6 ms）。3×100 ms 的余量足够，但**不能把缓冲降到 2 块**。
- 回调次数由本 tick 开始时的 `PendingBufferCount` 决定：欠载后一次会补 3 块，
  因此就绪队列深度必须 ≥3，不能只备 1 块。

> 参考实现：tML 自己的 `ASoundEffectBasedAudioTrack` 走的是「手动泵」——
> `Update()` 里 `if (IsPlaying && PendingBufferCount < 8) PrepareBuffer();`。
> 说明同一进程里两条路径都成立；我们选回调 + `Update()` 兜底的双保险。

### 2.2 `IsLooped` 是空实现 ✅ 与方案一致

`DynamicSoundEffectInstance.IsLooped` 的 getter 恒 `false`，setter 为空。
循环只能自己做（回绕填充），不能设标志位。

### 2.3 混响与变调对动态实例可用 ✅

`FAudioReverbController.ApplyCustomReverb` 只依赖 `inst.handle`、`inst.usingReverb`、
`inst.dspSettings` 三个成员，全部定义在 `SoundEffectInstance` 上；`DynamicSoundEffectInstance`
继承自它，`InitDSPSettings(format.nChannels)` 在构造时就完成分配。

两个必须注意的点：

1. **`Stop(immediate: true)` 会 `DestroyVoice` 并把 `usingReverb` 置 `false`**，
   同时 `ClearBuffers()` 清空队列。任何「停止后再播」都必须：重新预填 → `Play()` → 重新 `ApplyReverb`。
2. 单声道时 `SrcChannelCount == 1`，`ApplyCustomReverb` 只写 `outputMatrix[0]`，
   `FAudioVoice_SetOutputMatrix(..., 1, 1, ...)` 也合法 —— **单声道混响天然成立，不需要上混**。

### 2.4 NVorbis 在模组内可用 ✅（实机验证，不是推断）

- 编译期：`tMLMod.targets` 有 `<Reference Include="$(tMLLibraryPath)/**/*.dll" />`，全量引用。
- 运行期：`AssemblyManager.ModLoadContext.Load` 依次查 `AssemblyRedirects`（只含 tModLoader/FNA/
  Ionic.Zip/Steamworks.NET）、模组自带 `lib/*.dll`、依赖模组；未命中回落到默认 ALC，
  而 `tModLoader.deps.json` 的 TPA 里就有 `NVorbis/0.10.0.3`。

实机日志（服务器，`iso/tModLoader-Logs/server.log`）：

```
[PROBE] NVorbis assembly = NVorbis, Version=0.10.0.3, Culture=neutral, PublicKeyToken=null
[PROBE] decode location  = ...\.home\qa\iso\Libraries\NVorbis\0.10.0.3\NVorbis.dll
[PROBE] Sounds/probe_stereo.ogg: ok bytes=324612 ch=2 rate=48000 frames=1170846 decoded3x100ms=28800 readMs=0 decodeMs=12.54
[PROBE] Sounds/probe_mono.ogg:   ok bytes=434201 ch=1 rate=44100 frames=1910225 decoded3x100ms=13230 readMs=0 decodeMs=3.45
```

`bytes` 与磁盘文件大小逐字节相同 → `.tmod` 内 `.ogg` 没有二次压缩。

### 2.5 NVorbis API 与方案不符 ❌ 必须改

方案写的 `reader.ReadSamples(buf, 0, count, 2)`（第 4 参数做上混）在 `0.10.0.3` 里**不存在**。
实际签名只有：

```csharp
int ReadSamples(float[] buffer, int offset, int count);   // count 为交错样本数
long SamplePosition { get; set; }                         // DecodedPosition 已标记 Obsolete
VorbisReader(Stream stream, bool closeOnDispose = true)
```

因此正确做法是：**按文件真实声道数创建实例**（`(AudioChannels)reader.Channels`），
PCM 字节数 = `frames × channels × 2`。这也顺带绕开了 API 版 A1 的越界根因：
它把单声道样本按立体声写进 2048 样本的缓冲区，必然越界。

### 2.6 `.tmod` 条目可 seek ⚠️ 方案只说对了一半

- `TmodFile.ShouldCompress` 明确排除 `.png` / `.mp3` / `.ogg` → **ogg 原样存储**，
  取流时不会套 `DeflateStream`。
- `GetStream(name, newFileStream: true)` 返回的 `EntryReadStream` 包在 `FileStream` 上，`CanSeek == true`；
  但默认共享流是**互斥**的（`sharedEntryReadStream != null` 直接抛
  `Previous entry read stream not closed`），并发多条流必须用独立句柄。

结论：两条路都可行，推荐 `Mod.GetFileBytes(path)` → `MemoryStream`（一次读取、无句柄管理、
字节数 == 磁盘大小），代价是每活跃流常驻 ≤2.2 MB 压缩字节。若以后要做到 8 条以上活跃流，
再改成 `GetFileStream(..., newFileStream: true)` 省掉这份常驻。

### 2.7 资产实测（修正方案数字）

| 组 | 文件数 | 解码内存 | 与方案对比 |
|---|---:|---:|---|
| biome | **43**（39 子目录 + 4 根目录历史残留） | **369.2 MB** | 与方案 369 MB 一致 ✅ |
| environment | **5** | — | 方案写 12，实际 8（含 rain）❌ |
| rain | **3** | — | 同上 |
| 合计环境床 | **51** | **392.8 MB** | 方案 55 个 |

另外三个必须知道的事实：

1. **存在单声道文件**：`forest/day_3.ogg`（1ch / 44.1 kHz）。这就是 API 版 A1 会崩的那一类，
   必须进回归用例。
2. **采样率不统一**：39 条 biome 里 8 条是 48 kHz（`snow/*`、`cavern/wind`、`desert/critters`、
   `hell/rumbles`、`jungle/day`、`jungle/underground_night`），其余 44.1 kHz。
   实例采样率必须取文件值，写死 44.1k 会变速。
3. **4 个根目录历史文件**（`biome/forest_day.ogg` 等）代码零引用，
   解码 21.4 MB / 磁盘 2.3 MB，属可清理项（只影响包体，不影响运行内存）。

### 2.8 循环接缝：方案低估了 ❗

对 39 条 biome 逐条跑「2.2 圈回绕」，统计接缝处 `|Δsample|` 在全体相邻样本 Δ 分布中的分位：

| 判定 | 条数 |
|---|---:|
| 接缝 Δ > P99.5（可闻风险） | **10 / 39** |
| 其中 > P99.9 | 8 |

最差三条：`snow/windy` **12254**（满量程 32767 的 37%）、`crimson/rumbles` 2201、`hell/rumbles` 1310。
`forest/day_1` 1597 也超标 —— 而森林是最常待的群系。

**修法与实测效果**（等功率交叉淡化，循环重启点改为 `w`）：

| 宽度 | `snow/windy` 接缝 Δ | 判定 |
|---|---:|---|
| 无 | 12254 | 爆音 |
| 64 帧（1.33 ms @48k） | 321 | < P99.5 (461) ✅ |
| 256 帧（5.33 ms） | **114** | ✅ 最佳 |
| 441 帧（9.19 ms） | 198 | ✅ 但更长 |

原理：淡化把「尾部与头部混合」，使接缝落在源文件的自然相邻样本 `(w-1 → w)` 上，本质连续；
代价是循环长度缩短 `w` 帧（5.8 ms @44.1k），对 20–120 s 的环境床不可闻。
等功率（sin/cos）用于避免不相关噪声混合时的电平凹陷；线性淡化理论上有约 −3 dB 凹陷。

### 2.9 解码成本：决定「放工作线程」

单块 100 ms 的实测（含首次 JIT 的 max）：

| 文件 | 声道/采样率 | P50 | P95 | max | 预填 3 块 |
|---|---|---:|---:|---:|---:|
| forest/day_1 | 2 / 44.1k | 0.387 ms | 0.995 ms | 9.35 ms | 0.90 ms |
| forest/day_3 | 1 / 44.1k | 0.205 ms | 0.647 ms | 6.22 ms | 0.46 ms |
| snow/day | 2 / 48k | 0.213 ms | 0.874 ms | 8.69 ms | 0.46 ms |
| forest/morning_6 | 2 / 44.1k | 0.859 ms | 1.171 ms | 8.37 ms | 1.94 ms |

P95 ≈ 1 ms（单帧预算 16.6 ms 的 6%），但一次欠载会连补 3 块 → 最坏 27 ms 直接掉帧。
所以：**解码放工作线程，主线程只做 `SubmitBuffer`（一次 memcpy，约 19 KB）**。

---

## 3. 落地的具体形状

### 3.1 类与职责

```
Sounds/Streaming/OggStream.cs        单条流的解码/缓冲/提交/循环（骨架已编译通过）
Sounds/Streaming/StreamPool.cs       活跃流上限（4），按「Volume 持续为 0 最久」回收
Sounds/Streaming/StreamedAmbience.cs 与 ModAmbience 同形的公开面 + 音量包络
Sounds/Streaming/StreamAssets.cs     path → byte[] 缓存（ChangeTrack 复用，避免重复 GetFileBytes）
```

`StreamedAmbience` 的公开面（与现有调用点一一对应）：

```csharp
public float Volume;          // 由 Update 内的包络推进，写入 OggStream.Volume * Main.ambientVolume
public float MaxVolume;
public float VolumeStep;
public bool  PlayInMenu;
public string Name;
public bool  AreConditionsMet;
public bool  IsPlaying => Volume > 0f;
public void  ChangeTrack(string path);
public void  Update();        // 替代 API 的 INTERNAL_Update
```

### 3.2 线程模型（唯一容易写错的地方）

```
主线程                                  工作线程
──────────────────────────────────────  ────────────────────────────
Play(): 预填 3 块 → Play() → 启动 worker
                                        Fill() × N：VorbisReader.ReadSamples
                                        回绕：SamplePosition = 0（不 Stop）
FrameDispatcher.Update() → BufferNeeded
  ← 取 _ready，SubmitBuffer（主线程）
Update() 兜底：PendingBufferCount < 3 时再补
Shutdown：CTS.Cancel → Join(500) → Stop → Dispose
```

硬规则：

1. `VorbisReader` **只**在工作线程访问（非线程安全）。
2. `SubmitBuffer` **只**在主线程调用（`PendingBufferCount` 直接读 `List.Count`，无锁）。
3. 生产者只在 `_ready.Count < 3` 时补货，避免无限堆积。
4. 工作线程内 `catch` 一切异常并置 `Faulted`，绝不外抛到 `Main.Update`。

### 3.3 缓冲与内存

| 项 | 48 kHz 立体声 | 说明 |
|---|---:|---|
| 单块 PCM | 19,200 B | 100 ms × 48000 × 2ch × 2B |
| 在飞 + 池 | 4 块 × 19.2 KB ≈ 77 KB | 3 在飞 + 1 预备 |
| 解码缓冲 | 9600 × 4 B = 38 KB | `float[]` 单块 |
| 压缩字节 | ≤2.2 MB | `GetFileBytes` 的返回值 |
| **单流合计** | **≈ 2.3 MB** | 4 条流 ≈ 9.2 MB |

对比现状：43 条 biome 常驻 369.2 MB。即使只做到「流池 4 条」，也是 **40 倍**的下降。

### 3.4 与现有代码的接入点

| 位置 | 现状 | 流式后 |
|---|---|---|
| `AmbientHandler` 25 个字段 | `ModAmbience` | `StreamedAmbience`（同形） |
| `AmbientHandler.Ambiences` | `List<ModAmbience>` | `List<IAmbience>`（只用到 `Name`/`Volume`） |
| `MethodDetours.cs:82` 调试面板 | 遍历 `Ambiences` 取 `Name`/`Volume` | 不变（接口已覆盖） |
| `TerrariaAmbienceAPI.AllModAmbiences` | 25 条全注册，每帧被 API 驱动 | 自研流**不再注册**，顺带避开 A2/A6 |
| `FilterHelpers.ApplyReverb(instance)` | 每 tick 对 `SoundEffectInstance` 调用 | 零改动（动态实例是子类） |
| `AmbientPlayer` 水下 `Pitch` | `instance.Pitch = -0.05f` | 零改动 |
| `InitializeAllAmbienceToAvoidRuntimeOverhead()` | 24 条森林曲目全量预载 | 删除（P1） |

**不要**迁移的部分：199 个脚步 + 开箱/合成/NPC 叫声 + 97 个 wav 一次性音效。
它们要求同 tick 出声，且合计仅约 19 MB + 40 MB，收益低、调用点分散。

### 3.5 迁移边界与顺序

```
P3a  OggStream 单条接入 SnowAggro（接缝最差、最易听出问题）
P3b  StreamedAmbience + 面板切换（ForestDay 一组 4 条）
P3c  StreamPool 上限 4 + 回收策略
P4   其余 biome → environment/rain
```

---

## 4. 明确未验证的部分

不能凭反编译就宣称通过的四项：

1. **真实声卡上的听感**：本机只在服务端跑了解码，没有出声。混响/变调在 dynamic voice 上的
   实际效果、循环淡化是否真听不出，需要客户端实机。
2. **设备热切换**（换耳机/默认设备）：`SoundEffect.Device()` 重建后，动态实例的 `handle` 失效，
   需要「检 `IsDisposed` → 重建流 → 从当前位置续」。这条路径没有实测。
3. **暂停/最小化恢复**：`Game.Tick` 停摆期间 3 块缓冲会耗尽，恢复时需重新预填。
4. **长稳**：30 分钟内流数量、句柄数、内存是否单调上涨，需要实测。

另外：`Mod.GetFileBytes` 在**联机客户端**上读的是本地 `.tmod`，与服务器下发的模组版本
必须一致；版本不一致时 tML 本来就会拒绝加入，不构成额外风险。

---

## 5. 下一阶段的验证矩阵

| 项 | 方法 | 通过标准 |
|---|---|---|
| 编译 | 隔离副本 `dotnet build -p:BuildMod=false` | 0 error（骨架已验证） |
| 单流出声 | 客户端 + `snow/windy` 单条 | 连续 10 分钟无爆音、无静音 |
| 欠载 | `OggStream.Underruns` 计数 | 10 分钟内 0 |
| 接缝 | 耳听 + 离线 Δ 统计 | 无周期性咔哒 |
| 内存 | 4 检查点（菜单/森林/10 分钟/巡游后） | 常驻 ≤ 4 条流 |
| 切换 | 森林→雪原→丛林→森林 ×5 | 无静音、无 >1 帧卡顿 |
| 混响/变调 | 室内、下水 | 与改动前听感一致 |
| 设备切换 | 播放中切默认输出 | 自动恢复，无需重进世界 |
| 长稳 | 30 分钟 | 流数/句柄/内存无单调上涨 |

---

## 6. 对原方案 `docs/streaming-plan-2026-11-19.md` 的修正清单

| # | 原文 | 修正 |
|---|---|---|
| 1 | `ReadSamples(buf, 0, count, 2)` 让 NVorbis 上混 | 该重载不存在；改为按文件声道建实例，单声道原生支持 |
| 2 | 「单声道处理是 API 版 A1 崩溃的根因」 | 结论对，但修法不是上混，而是缓冲按 `frames × channels × 2` 计算 |
| 3 | `FrameworkDispatcher` 在 `Game.Tick` 里被调用（2 处） | 实际在 `Game.Update`，全程序集 1 处；`Main.Update` 通过 `((Game)this).Update` 调用它；泵在 `DoUpdate` 之后 |
| 4 | `.tmod` 内部条目不可 seek | 仅压缩条目如此；`.ogg` 不压缩，`GetFileStream(newFileStream:true)` 可 seek；推荐 `GetFileBytes` |
| 5 | 「预填 300 ms 即出声」 | 成立，但**必须**在 `Play()` 前预填；`Play()` 后的 `Update()` 因 `State==Stopped` 不会回调 |
| 6 | 「接缝加 64 帧交叉淡化做保险」 | 不是保险而是必需：39 条中 10 条超标，最差 12254；建议 256 帧 |
| 7 | environment 12 条 / 合计 55 条 | 实际 environment 5 + rain 3 = 8，合计 51 条 / 392.8 MB |

---

## 附录：证据文件

| 路径 | 内容 |
|---|---|
| `.home/qa/out/stream-verify/dyninst.cs` | FNA `DynamicSoundEffectInstance` 全量反编译 |
| `.home/qa/out/stream-verify/sei2.cs` | `SoundEffectInstance`（Stop/Dispose/dspSettings） |
| `.home/qa/out/stream-verify/asoundtrack.cs` | tML 自己的动态音轨（手动泵参考实现） |
| `.home/qa/out/stream-verify/oggtrack.cs` | tML 的 `OGGAudioTrack`（NVorbis 用法参考） |
| `.home/qa/out/stream-verify/tmodfile.cs` | `TmodFile` 压缩策略与流语义 |
| `.home/qa/out/stream-verify/asmmgr.cs` | 模组程序集解析链 |
| `.home/qa/out/stream-verify/seam-summary.tsv` | 39 条 biome 的采样率/声道/P99.5/接缝 Δ |
| `.home/qa/out/stream-verify/mem.tsv` | 51 条环境床的 frames × channels × 2 |
| `.home/qa/iso/tModLoader-Logs/server.log` | `[PROBE]` 实机日志 |
| `.home/qa/stream-harness/` | 离线解码器（`oggprobe`）与 `--xfade` 模式 |
| `.home/qa/stream-verify-tool/` | IL 调用点扫描器（`probe`） |
| `.home/qa/iso/ModSources/TerrariaAmbience/Sounds/Streaming/OggStream.cs` | 编译通过的骨架（未进入仓库） |

> 上述 `.home/**` 全部在 `.gitignore` 内，可整目录删除，不影响仓库与游戏。
