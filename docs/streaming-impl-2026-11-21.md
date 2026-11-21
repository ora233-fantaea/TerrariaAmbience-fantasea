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

失败时还会执行**第二层兜底**：在同一条音轨内部改用常驻 `SoundEffectInstance`（`IsLooped = true`），
公开面不变，音量梯度与水下变调照旧生效。这样最坏情况只是多占一条音轨的内存，不会变成没声音。
兜底只尝试一次；再失败就保持静音，不递归。

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

## 9. 打包：可复现配方（已打通）

### 9.1 为什么之前会失败

`dotnet tModLoader.dll -build <dir>` 会**自己先编译一遍**。这条路径下 `Krafs.Publicizer`（csproj 里的
`<Publicize Include="FNA" />`）没有生效，于是 `FNAPlatform` / `MarshalHelper` / `SoundEffectInstance.handle`
这些 FNA 内部成员全变成不可访问，报 25 个错——全部落在本轮未改动的文件里。

单独验证过：`dotnet build -t:Compile -p:BuildMod=true` **零错误**，所以问题不在 `tMLMod.targets`，
而在 `-build` 触发的那次编译。

### 9.2 正确做法：分两步，打包时复用已编译 DLL

`tMLMod.targets` 自己的 `BuildMod` 目标就是这么调的（`-eac $(TargetPath)`），照抄即可：

```bat
:: 1) 只编译，不打包
dotnet build TerrariaAmbience.csproj -c Release -p:BuildMod=false

:: 2) 打包，-eac 指向刚编译出的 DLL（跳过第二次编译）
dotnet tModLoader.dll -build "<源目录>" ^
  -eac "<源目录>\bin\Release\net8.0\TerrariaAmbience.dll" ^
  -unsafe true -savedirectory "<隔离 savedir>"
```

生效标志：输出里出现 `Loading pre-compiled ... from TerrariaAmbience.dll` 后直接 `Packaging:`，
中间没有 `Compiling`。

**目录名必须是 `TerrariaAmbience`。** `-build` 用文件夹名当模组内部名；本仓库目录叫
`Terraria-Ambience-Stream-branch`，直接从仓库根打包会得到内部名错误、且 `build.txt` 之外的元数据也按文件夹名走。
所以先复制/链接出一个同名目录再打包。

### 9.3 实测结果

| 项 | 值 |
|---|---|
| 产物 | `TerrariaAmbience.tmod`，63,981,018 B |
| 内部名 | `TerrariaAmbience`（`Adding Content: Terraria Ambience v0.3.7.1` 可证） |
| 资源条目 | `Sounds/Custom/ambient/biome/snow/windy.ogg` 等，与代码拼接的路径逐字一致 |
| 服务器加载 | `Adding Content` → `Finalizing Content` → `Adding Recipes`，零异常 |
| 客户端加载 | 独立 tML 2026.9.3.1 客户端进到主菜单，模组正常加载 |

### 9.4 独立运行时的准备（不碰游戏安装目录）

```bat
:: 官方发布包自带 Content 与 Libraries，只有 61 MB
curl -L -o tModLoader.zip ^
  https://github.com/tModLoader/tModLoader/releases/download/v2026.09.3.1/tModLoader.zip
unzip tModLoader.zip -d runtime
```

发布包**不含 `dotnet/`**，用系统 dotnet 启动即可；`-savedirectory` 指到工作区，
`-steamworkshopfolder` 指到一个空目录，`tModLoader-Logs` 会落在 DLL 同级目录（即工作区）。

客户端找模组的目录是 **`<savedir>/tModLoader/Mods`**，不是 `-modpath` 指定的那个（实测 `-modpath` 对客户端无效，
它会自己新建 `<savedir>/tModLoader/Mods` 并写一个 `enabled.json = []`）。服务器模式才认 `-modpath`。

ModConfig 文件名是 `<模组名>_<配置类名>.json`，放在 `<savedir>/tModLoader/ModConfigs/`。
本轮用它把 `streamAmbientBeds` 置为 `true`，日志随即从
`Ambience streaming: disabled, 25 resident` 变成 `enabled, 1 streamed / 24 resident`。

## 10. S3：森林四条 + 去掉预载

### 10.1 改动

| 项 | 内容 |
|---|---|
| 迁移 | `ForestMorning` / `ForestDay` / `ForestEvening` / `ForestNight` 增加流式孪生字段，按 `StreamingEnabled` 二选一 |
| 换曲入口 | 新增 `SetForestMorningTrack` 等四个方法，网络包与黎明/黄昏都改走它们，不再直接碰字段 |
| 赋值收口 | `SetMaxVolume(resident, streamed, value)`，避免每个梯度都要写两遍空判断 |
| 预载 | `InitializeAllAmbienceToAvoidRuntimeOverhead()` 从 `Mod.Load` 挪到 `AmbientHandler.Initialize`，并且**只在关闭流式时执行** |

预载之所以敢跳掉：流式的 `ChangeTrack` 把「打开文件 + 建解码器 + 预填 3 块」全放在工作线程，
黎明/黄昏换曲不会卡主线程；而常驻实现在 `ChangeTrack` 里是 `ImmediateLoad`，没预载就会当场同步解压。

### 10.2 独立运行时实测（2026-11-21 15:00 前后）

环境：官方发布包 `v2026.09.3.1` 解到工作区，自带 Content 与 Libraries，系统 dotnet 8.0.426，
隔离 savedir，`-steamworkshopfolder` 指向空目录。游戏在隐藏桌面运行，不弹窗、不抢焦点。

| 项 | 结果 |
|---|---|
| 模组加载 | `Adding Content: Terraria Ambience v0.3.7.1` → `Finalizing` → 主菜单，零异常 |
| 开关关闭 | `Ambience streaming: disabled, 25 resident` |
| 开关开启 | `Ambience streaming: enabled, 5 streamed / 20 resident` |
| 工作集（关闭） | 2,230.9 → 2,286.9 MB（两次采样） |
| 工作集（开启） | 1,343.0 → 1,370.1 MB（两次采样） |
| 差值 | 约 **917 MB** |

两点必须说清：

1. 这是同机、同包、同相位（主菜单）、各两次采样的 A/B，只改了 `streamAmbientBeds`。
2. 其中**确定**属于本改动的部分，是按解码体积算的约 236 MB（24 首森林预载 198 MB + 5 条不再常驻约 38 MB）。
   剩余差异来自托管堆与原生分配器的行为，没有单独归因，不要当成「流式一定省 900 MB」的通用结论。

### 10.3 仍未验证

- 真实游玩下的换曲听感（黎明/黄昏各一次 `ChangeTrack`），需要进森林世界；
- 4 条流同时活跃时的欠载计数，需要走到森林昼夜交界并叠加风雪；
- 真机听感、设备热切换、长稳，同 §8.1。

## 11. S4：全部迁移与依赖收敛

### 11.1 改动

| 项 | 内容 |
|---|---|
| 字段 | 25 条环境床的字段类型统一为 `StreamedAmbience`，删除 5 个孪生字段 |
| 集合 | `Ambiences` 改为 `List<StreamedAmbience>`，删除 `StreamedAmbiences` |
| 初始化 | 两处 `if (StreamingEnabled) ... else ...` 收成单一路径，25 条全部 `new StreamedAmbience` |
| 换曲 | 删除 `SetForestXTrack` 与 `SwitchTrack`，调用点回到直接的 `ForestX.ChangeTrack` |
| 变调 | `Desert.SoundInstance.Pitch` 等改为 `Desert.Pitch` 属性，起播前后都成立 |
| 关闭流式 | 不再保留第二套实现：`StreamedAmbience.ForceResident = true`，所有音轨走「常驻兜底」同一条代码 |
| 上限 | `MaxActiveStreams` 4 → 8（单条流约 75 KB，8 条也才 600 KB） |

`ModAmbience` 在代码里已经没有任何引用，只剩注释里的说明。
`TerrariaAmbienceAPI` 仍是硬依赖，但用途只剩脚步/配置等，与环境床无关。

### 11.2 关闭流式时到底发生什么

`AmbientConfig.streamAmbientBeds = false` 不再切换实现，而是：

1. `ForceResident = true`；
2. 森林 24 首仍然预载（`InitializeAllAmbienceToAvoidRuntimeOverhead`）；
3. 每条音轨在「第一次真的要出声」时，用 `Assets.Request<SoundEffect>(ImmediateLoad)` 取常驻副本。

因为预载已经把它们读进来了，这一步只是拿缓存，没有解码开销，所以换曲不会卡。
与 1.4 的差别只在「25 条实例不在初始化时全部创建」，内存反而更省。

### 11.3 独立客户端实测（同一台机、同一个包，只改一个开关）

| 配置 | 工作集（主菜单） | 日志 |
|---|---|---|
| 流式开启（默认） | **1,107.5 MB** | `Ambience beds: 25 tracks, streaming=enabled` |
| 流式关闭（兜底） | 1,778.4 MB | `Ambience beds: 25 tracks, streaming=disabled (resident fallback)` |

对照此前两次：S3（5 条流式 + 20 条常驻 + 预载）为 1,370.1 MB；S2（25 条常驻 + 预载）为 2,286.9 MB。

即相对未改造前，主菜单工作集下降约 **1.18 GB**；相对 S3 再降约 **263 MB**。
两种配置下模组都正常加载、零异常。

### 11.4 仍未验证

- 真实游玩下的群系巡游与换曲听感（听不到，只能你验）；
- 8 条流上限的实际并发（现实中约 5 条）；
- 设备热切换、最小化恢复、30 分钟长稳。

### 11.5 后续可做

1. 把 `AmbientConfig` 里的 `streamAmbientBeds` 说明写进 README（目前只写在代码注释里）。
2. 若确认稳定，删掉 `InitializeAllAmbienceToAvoidRuntimeOverhead` 与 `using TerrariaAmbienceAPI` 里已无用的部分。

## 12. 主菜单音乐叠两层（2026-11-21 16:00 修复）

### 12.1 现象

主菜单听主题曲时，Alt-Tab 出去再回来，会同时听到**主题曲 + tModLoader 自带菜单曲**（50 号槽）。
一旦出现就不会自己消失，直到切换菜单主题或离开菜单。

### 12.2 根因（用户本机日志直接给出现场）

`MenuMusicGuard` 把「暂停」当成了「坏死」，然后叠了一层兜底曲，而兜底曲从不退场。

关键日志（`tModLoader-Logs/client.log`，15:44:00–05，`menu=CalamityMainMenu want=119`）：

| 时间 | 内容 |
|---|---|
| 15:44:00.115 | `已硬重启 … fade=0.04 pending=0 dead=True`（硬重启后队列为空，正常） |
| 15:44:00.951 | `正常播放 … fade=0.46 active=False pending=2 dead=False` |
| 15:44:01.202 | `重建音轨 119 失败: NullReferenceException` |
| 15:44:01.286 | `want=119 重建后仍无声部数据，回退到默认菜单曲 50` |
| 15:44:05.972 | `正常播放 … fallback=True` ← 主题曲恢复，兜底曲还在响 |

三处缺陷叠加：

1. **失焦 = 暂停，却被判死。** 失焦时原版每帧 `audioSystem.PauseAll()`；FNA 的 `Pause()` 走
   `FAudioSourceVoice_Stop`，会冲刷动态声部的队列，`PendingBufferCount` 与 `BuffersQueued`
   同时归零、`State` 变 `Paused`。旧逻辑只看 `pending == 0 && queued == 0`，在失焦期间必然判死。
2. **兜底只上不下。** `RunFallback` 每帧把 `musicFade[50]` 顶成 1 并起播 50 号曲，但从不检查
   主题曲是否已经恢复，也从不停止 50。回焦后 `ResumeAll` 会把两条一起恢复 → 两首同响。
3. **重建路径本身是坏的。** `FindMusicPath` 直接 `foreach (mod.GetFileNames())`，而
   `Mod.GetFileNames()` 在 `File == null` 的模组上返回 null → NRE → 重建失败 → 恰好触发了兜底。

另外，守护的时间常数按**帧**计：`CheckInterval=20`、`LogInterval=300`。在 250 fps 的机器上
"每秒 3 次检查"实际是 12 次/秒，"连续 1 秒无数据"只有 0.24 秒；在无 vsync 的隐藏桌面上
（实测约 1 万次 Update/秒）日志会刷到 450 行/秒。判死因此比设计意图激进一个数量级。

### 12.3 改动

| 项 | 改动 |
|---|---|
| 失焦 | `Main.instance.IsActive == false` 时整段跳过，不推进任何计时、不做任何判定 |
| 暂停 | `IsPaused` 一律视为存活；`VoiceDiag` 在 `State == Paused` 时直接返回 `Dead=false` |
| 兜底 | 主题曲一旦"在播 + 未暂停 + 有缓冲"就撤销兜底并 `Stop(Immediate)` 掉 50 号曲 |
| 兜底 | `want == 50`（主题本身就是默认曲）时禁用兜底，不干预 |
| 换主题 | 换主题时先 `StopFallback`，避免上一轮兜底曲残留 |
| 计时 | 改用 `gameTime.ElapsedGameTime`：检查 0.33 s、状态行 5 s，与帧率无关 |
| 重建 | 先摘槽位、失败时把旧对象放回；`FindMusicPath` 跳过 `File == null` 的模组并逐项 try/catch |
| 反射 | `_soundEffectInstance` 的 `FieldInfo` 改成静态缓存（兜底期间每帧都会读一次） |

### 12.4 验证（隔离运行时，`MenuProbe` 模组）

探针提供一条模组菜单主题曲（SE 动态音轨，与 Coralite/Calamity 同类，实测槽位 92），
并在菜单静置 8 s 后把 `Game.IsActive` 置 false 保持 6 s 再置回，随后观察 12 s；
三个检查点直接读 `audioSystem.IsTrackPlaying(50)`。

| 版本 | `slot50Playing`（失焦前/回焦后/结束） | `fallback=True` | 守护日志行数 |
|---|---|---|---|
| 修复前（`e197229` 构建） | 首个对照跑次即出现 `fallback=True` + `正常播放` 并存 | 出现 | **66,390 行 / 135 s**（约 450 行/秒） |
| 修复后 | `False / False / False` | **0 次** | **8 行 / 整个会话** |

注：修复前的症状是竞态，并非每次都能复现（另一次对照跑次未触发）。这与"用户偶尔遇到"一致；
修复后该路径被结构性地关掉，不再依赖时序。

### 12.5 未覆盖

- 真实 Alt-Tab（而非模拟 `IsActive=false`）的听感仍需人工确认；
- Steam 覆盖层、窗口最小化走的是同一条 `IsActive` 通路，按推理应同样修复，但未单独实测。
