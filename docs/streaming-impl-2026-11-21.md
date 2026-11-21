# 流式环境床：S1–S2 实现与验收

> 分支 `stream-1.4` ｜ 2026-11-21 ｜ 对应 §11 的 S1（解码内核）与 S2（单条打通）

## 1. 本轮做了什么

| 项 | 状态 |
|---|---|
| `OggStream` 解码内核 | 完成，离线回归全部通过 |
| `BlockQueue` 线程交接 | 完成，20 万块并发压测通过 |
| `StreamedAmbience` 播放器 | 完成，状态机 + 故障隔离 |
| `SnowAggro` 接入 | 完成，由配置开关控制，默认关闭 |
| `1.4` 行为 | 未改变（开关关闭时走原路径） |

代码全部落在 `Sounds/Streaming/`，除四处接线外没有触碰既有逻辑。

## 2. 新增文件

| 文件 | 行数 | 职责 |
|---|---|---|
| `Sounds/Streaming/OggStream.cs` | ~330 | 解码内核。只吃流、吐 PCM16，不碰设备、不起线程，可脱离游戏编译 |
| `Sounds/Streaming/BlockQueue.cs` | ~95 | 定长 PCM 块的四槽环形交换区，解码线程与主线程唯一的交接面 |
| `Sounds/Streaming/StreamedAmbience.cs` | ~420 | 播放器。音量包络、状态机、缓冲泵送、故障隔离 |

接线点：

- `Core/AmbientConfig.cs`：新增 `streamAmbientBeds`（`[ReloadRequired]`，默认 `false`）
- `Content/AmbientAndMore/AmbientHandler.cs`：新增 `StreamedAmbiences` 集合与 `SnowAggroStream`，两套循环并存
- `TerrariaAmbience.cs`：卸载时 `StreamedAmbience.DisposeAll()`
- `Core/MethodDetours.cs`：调试面板追加流式音轨与欠载计数

## 3. 三条会改变实现的事实

### 3.1 NVorbis 的 seek 只在位置 0 可靠

这是本轮最重要的发现，直接否定了原方案「打开时 seek 到 `total-W` 取尾部」的做法。

实测（`snow/windy.ogg`，48 kHz，1,170,846 帧），用 `SamplePosition` 定位后逐样本比对顺序解码结果：

| 目标位置 | 距文件末尾 | 最大误差 | 结果 |
|---|---|---|---|
| 0 | 1,170,846 | 0 | OK |
| 256 | 1,170,590 | 0 | OK |
| 585,423 | 585,423 | 0 | OK |
| 1,154,462 | 16,384 | 0 | OK |
| 1,166,750 | 4,096 | 11,909 | **错位** |
| 1,170,590 | 256 | 12,512 | **错位** |

进一步在 `rain/light.ogg` 上复测，情况更糟：

| 目标位置 | 最大误差 | 结果 |
|---|---|---|
| 0 | 0 | OK |
| 256 | 2,148 | **错位** |
| 4,096 | 1,793 | **错位** |
| 124,238 | 0 | OK |
| 248,477（中点） | 1,859 | **错位** |
| 480,570 | 0 | OK |
| 496,698（距末尾 256） | 1,242 | **错位** |

这条文件连中点都错。原因是它的 **granule 元数据本身有问题**：`TotalSamples` 报 496,954，
而顺序解码实际得到 **497,402 帧**（多 448 帧，约 9.3 ms）。granule 表错了，seek 的落点自然跟着错。

另外，在尾部附近反复 seek 会长时间卡住（实测 `total-260` 超过 100 秒无返回）。

**结论：NVorbis 的 seek 只有位置 0 可以被信任。** 实现里因此只保留一种 seek——回到 0，
之后用顺序解码丢弃 W 帧来「跳回第 W 帧」。代价是每圈多解 256 帧（约 30 µs），换来对元数据损坏文件的正确性。

### 3.2 循环实现改为「接缝合成 + 丢帧回绕」

正常区播到 `total-W` 时，解码器正停在该处，顺序读 W 帧即为尾部；与打开时缓存的头部 W 帧做等功率混合，
作为接缝输出，然后 `SeekToFrame(0)` 并顺序丢弃 W 帧，继续下一圈。

这样每圈输出 = `[0, total-W)`（首圈）或 `[W, total-W)`（后续圈）+ W 帧接缝，
周期恒为 `total-W`，前后两处交界都落在自然波形的相邻样本上。

### 3.3 API 里已有一套 `ModStreamedAmbience`，但不能用

`lib/TerrariaAmbienceAPI.dll` 里已经存在 `ModStreamedAmbience`（`MaxActiveStreams = 4`、`DynamicSoundEffectInstance`、4 块预填）。它验证了技术路线，但直接使用有三个硬伤：

1. **每帧 `Main.NewText`**：`UpdateAll` 里对每条音轨、每次 `Volume != 0` 都发一条聊天消息，会刷屏。
2. **依赖 `Mod.SourceFolder`**：`Path.Combine(Mod.SourceFolder, ...)` 后交给 `ModContent.OpenRead`，只有从源码目录加载时才有值；发布版 `SourceFolder` 为空，`Path.Combine(null, ...)` 直接抛异常。
3. **异常外抛**：`Volume` 的 setter 里直接调 `TryStartStreaming`，内部的 catch 是 `throw`；一旦资源缺失，异常会顺着属性赋值穿到 `Main.Update`。

因此仍按原计划自研，但吸收了它正确的部分（块大小、预填数量、`DynamicSoundEffectInstance` 用法）。

## 4. 已验证的运行时契约

从 FNA 与 tML 的反编译结果逐条确认，这些是实现的硬约束：

| 契约 | 依据 |
|---|---|
| `DynamicSoundEffectInstance.IsLooped` 是空实现 | getter 恒 false、setter 空；循环必须自己回绕 |
| `Play()` 先调 `Update()` 再建声源 | `Update()` 在 `State == Stopped` 时提前返回，所以 `Play()` 前不会触发 `BufferNeeded` |
| `Stopped` 状态下 `SubmitBuffer` 只记账 | 记入 `queuedSizes`，`Play()` 时由 `QueueInitialBuffers` 真正提交，因此预填必须在 `Play()` 之前 |
| `SubmitBuffer` 会拷贝 | `Marshal.Copy` 进原生内存，托管数组可立即复用（块池的前提） |
| `BufferNeeded` 只在 `State == Playing` 时触发 | 且最多补到 3 块 |
| `GetFileStream(name, newFileStream: true)` 开独立句柄 | 内部 `File.OpenRead(path)` + `EntryReadStream`，不与共享流互斥 |
| 大于 128 KB 的条目不会进内存缓存 | `cachedBytes` 仅在 `CompressedLength <= 131072` 时填充，`.ogg` 走真实文件流 |
| `EntryReadStream.CanSeek` 透传底层流 | 独立句柄下可寻址，NVorbis 才能初始化 |

## 5. S1 验收结果

工具：`.home/qa/stream-s1/`（编译仓库里的真实 `OggStream.cs`，不是拷贝）

判定：只看两个交界点（进入接缝、离开接缝）的样本差，阈值取整段信号的 P99.9。
接缝内部是真实音频的等功率混合，把它也算进去测的是素材本身。

| 指标 | 结果 |
|---|---|
| 覆盖文件 | 51 条（biome 43 + environment 5 + rain 3） |
| 通过 | **51 / 51** |
| 逐帧一致性 | 用「全量顺序解码」构造参考实现，渲染 3 个周期逐帧比对，**最大误差 0** |
| tML 流语义下的一致性 | 用 `EntryReadStream` 的等价语义（`Length` 只算条目、`SeekOrigin.End` 为 `Length-offset`）重跑，**最大误差仍为 0** |
| 帧数契约 | 全部 51 条 `ReadPcm16` 返回值恒等于请求帧数 |
| 解码吞吐 | 约 8,000 kframes/s（实时需求的 160 倍以上） |
| 单块耗时 | 100 ms 块最大 3.6 ms，典型 0.2–0.9 ms |

**原生回绕本身就会响的 9 条**（Δ 超过素材自身 P99.9）：

| 音轨 | 原生 | 淡化后 | P99.9 | 改善 |
|---|---|---|---|---|
| `snow/windy` | 12,254 | 254 | 559 | **48×** |
| `hell/rumbles` | 1,713 | 65 | 191 | 26× |
| `crimson/rumbles` | 2,201 | 94 | 511 | 23× |
| `underwater_loop` | 1,352 | 80 | 191 | 17× |
| `cavern/wind` | 163 | 10 | 79 | 15× |
| `forest/morning_1` | 175 | 14 | 31 | 12× |
| `forest/evening_1` | 575 | 53 | 143 | 11× |
| `breeze_heavy` | 4,823 | 784 | 1,743 | 6.1× |
| `beach/waves_calm` | 291 | 89 | 223 | 3.2× |

`snow/windy` 的原生回绕差 12,254 相当于满量程的 37%，是一声明确的爆音。

其余 42 条原生回绕本就在阈值内，淡化后同样保持在阈值内。全量里交界点最大的是 `rain/heavy` 的 1,108，
而该文件的 P99.9 是 7,855——它本身就是噪声型素材，样本差天然大。

`BlockQueue` 并发压测：4 槽环形缓冲、20 万块、生产者与消费者各一线程，**零丢失、零错序、零串块**，耗时 133 ms。

## 6. S2 接入方式

```
AmbientConfig.streamAmbientBeds = false（默认）
  → SnowAggro 仍为 ModAmbience，行为与 1.4 完全一致
AmbientConfig.streamAmbientBeds = true（需重载模组）
  → SnowAggro 不创建，改由 StreamedAmbience 接管
```

`AmbientHandler` 里 `Ambiences`（常驻）与 `StreamedAmbiences`（流式）两套循环并存，
两条路径的包络语义逐行对齐：`MaxVolume` 每帧重算、乘房间与总音量系数、水下 `Pitch = -0.05`、音量再乘 0.1。

状态机：

```
Idle ──(volume > 0)──> 起后台线程 ──(预填 3 块)──> 主线程建实例、提交、Play
                                                        │
                                        ┌───────────────┴───────────────┐
                                        │                               │
                                  正常补块                        解码/提交失败
                                        │                               │
                                   (volume ≤ 0) ──> 停机 <──── Failed（不再重试）
```

启动成本被摊到两帧：工作线程负责打开文件、建解码器、预填 3 块；主线程只在就绪后建实例并 `Play()`，避免起播时卡一帧。

失败即终止：`Failed` 后不再自动重试（避免故障循环），需 `ChangeTrack` 重置。

## 7. 怎么测

1. 游戏内 设置 → 模组配置 → Terraria Ambience → `Streaming` → 勾选 `streamAmbientBeds`
2. 重载模组（该选项标了 `ReloadRequired`）
3. 进日志确认一行：`Ambience streaming: enabled, 1 streamed / 24 resident`
4. 进雪原，等下雨（或用流程控制调到雨天）
5. 打开调试界面（`debugInterface`），`SnowAggro` 一行会显示 `(u0)`；`u` 后面的数字是累计欠载次数

验收标准：刮风音轨连续响 10 分钟，`u` 保持 0；切出雪原后应淡出至停，`u` 不再增长。

## 8. 未验证项与遗留问题

### 8.1 未验证

| 项 | 原因 |
|---|---|
| 真实声卡上的听感 | 无法无人值守判断 |
| 设备热切换 / 最小化恢复 | 需要真实设备事件 |
| 30 分钟长稳 | 需要真实游玩 |
| 打包为 `.tmod` | 见 §9 |

### 8.2 素材数据问题（1/51）

`rain/light.ogg` 的 OGG granule 元数据不可靠：
`TotalSamples` 报 496,954，顺序解码实际得到 497,402 帧，**多 448 帧（约 9.3 ms）**。

影响：该曲每圈循环会少播尾部 9.3 ms，且失效的 granule 表使非零位置的 seek 全部错位（见 §3.1）。
由于实现已改成「只 seek 到 0」，音频内容是正确的，只是尾巴上这一小段永远听不到。
9.3 ms 对一条 10 秒的雨声循环不可分辨，因此不做特殊处理，仅登记。

## 9. 已知环境问题（与本次改动无关）

用 `tModLoader.dll -build` 打包会失败，报 25 个 FNA 内部成员不可访问的错误（`FNAPlatform`、`MarshalHelper`、`SoundEffectInstance.handle` 等）。
这些文件本轮完全没有改动（`FAudioReverbController`、`FilterHelpers`、`MenuMusicGuard`），
原因是 `BuildMod=true` 这条路径下 csproj 里的 `Krafs.Publicizer`（`<Publicize Include="FNA" />`）没有生效，
而 `dotnet build -p:BuildMod=false` 是正常的。

另外 tML 的 `-build` 按**文件夹名**匹配 csproj：目录必须叫 `TerrariaAmbience`，否则会退化成不带包引用的默认编译。
本仓库目录名为 `Terraria-Ambience-Stream-branch`，因此直接打包会走错路径。

结论：打包请在 tModLoader 界面里进行，或把目录名改为 `TerrariaAmbience` 后放入 `ModSources`。
本轮只保证 `dotnet build -p:BuildMod=false` 通过、程序集内包含三个新类型。

## 10. 下一动作（S3）

1. 把 `StreamedAmbience` 从「单条」扩到「流池」：`Active` 上限 4，超出时保持静音等待，不抢占。
2. 迁森林 4 条（`ForestMorning/Day/Evening/Night`），验证 `ChangeTrack` 在黎明/黄昏切换时不爆音。
3. 观察 `ChangeTrack` 的当前实现是「停掉重开、从 0 开始」。若切换瞬间可闻，再补位置继承。
