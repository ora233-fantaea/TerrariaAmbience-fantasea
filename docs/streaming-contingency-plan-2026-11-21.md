# 流式落地风险与预案（2026-11-21）

> 分支：`stream-1.4`（独立工作副本 `D:\Terraria-Ambience-Stream-branch`）
> 状态：**准备阶段，不含任何实现代码**。
> 目的：把「会失败的地方」先钉在纸上，每条给出可执行的兜底路径与验证方式。

---

## 0. 风险登记表（按爆炸半径排序）

| 编号 | 风险 | 触发条件 | 影响 | 预案 | 级别 |
|---|---|---|---|---|---|
| R1 | 回调抛异常直穿到 FNA | `BufferNeeded` 或每 tick 更新内抛任何异常 | **整个游戏崩溃**（不在 `DoUpdate` 的 catch 内） | 回调体全包 `try/catch`；异常只置 `Faulted` 标志，绝不上抛 | 高 |
| R2 | 工作线程误调 FNA | 在解码线程里 `Play/Stop/Dispose/Device()` | `ThreadStateException`，voice 状态损坏 | 只允许主线程调 FNA；工作线程只碰 `VorbisReader` 与纯托管数组 | 高 |
| R3 | 卸载/热重载时线程未退 | 退世界、换模组、`Unload()` | `Collectible` ALC 无法回收；随机崩溃 | `Unload()` + `OnWorldUnload` 里 `Cancel → Join(500) → Dispose`，并把线程标 `IsBackground` | 高 |
| R4 | 资源读取撞共享流 | 用 `Mod.GetFileBytes` 且同时有别的读取 | `IOException: Previous entry read stream not closed` | 不用 `GetFileBytes`；改用 `GetFileStream(path, true)` 独立句柄（tML 自己就这么做） | 中 |
| R5 | 原生缓冲泄漏 | 预填了 3 块但 `Play()` 没成功 | 每实例约 57 KB 非托管内存不回收 | 预填与 `Play()` 放在同一段 `try` 里；`Device()` 探活成功后才分配缓冲 | 中 |
| R6 | 无音频硬件 / 设备被禁用 | `FAudioContext.Create()` 失败 | `NoAudioHardwareException`；游戏本身也已无声 | 构造前 `try { SoundEffect.Device(); } catch { 关闭流式 }`；降级到常驻（同样无声）并只记一次日志 | 中 |
| R7 | 设备热切换 | 播放中换默认输出设备 | 声音消失且 FNA 无重建入口 | **不在本模组解决**：与 tML 自带音乐同命运；检测到句柄失效就重建一次，失败则本会话关闭流式 | 中 |
| R8 | 切曲与流池竞争 | 黎明/黄昏 `ChangeTrack`，或雨雪叠加 | 切曲瞬间双流、静音、池抖动 | 池上限 4 但允许 1 条“过渡位”（实际 5）；新流预填成功后才停旧流 | 中 |
| R9 | 暂停/最小化后缓冲耗尽 | `Game.Tick` 停摆数秒 | 恢复瞬间欠载、咔哒 | 恢复时先补满 3 块再继续；欠载按静音处理并计数 | 低 |
| R10 | 混响在动态实例上不生效 | `SetOutputVoices` 对动态 voice 的行为差异 | 室内环境床失去混响 | 每 tick 检查 `handle` 变化即重挂；连续 3 次失败则本流回退常驻 | 低 |
| R11 | 音量包络与 API 不一致 | 重写 `INTERNAL_Update` 时漏掉 `Main.ambientVolume` / 焦点淡出 | 音量不升、不降、或菜单里有声 | 逐字段对照反编译源码移植，加一条「Volume 曲线与旧实现逐 tick 相等」的离线断言 | 低 |
| R12 | 内存不降反升 | `byte[]` 缓存随世界反复加载累积 | 每进一次世界多 10–60 MB | 缓存挂在 `ModSystem` 上，`OnWorldUnload` 清空；只缓存当前池内用到的条目 | 低 |
| R13 | 池容量不足 | 雪天+刮风+水下+室内混响同时 | 频繁回收、可闻的重启 | 上限 4 → 5（含过渡位）；超限时优先保留 `Volume` 最高的一条 | 低 |
| R14 | 联机两端资源不一致 | 客户端与服务端 `.tmod` 版本不同 | tML 本身就会拒绝加入，不额外处理 | 仅在日志里提示；不写补偿逻辑 | 低 |

---

## 1. 关键 API 事实（本轮补查，均带出处）

### 1.1 异常边界：`Main.Update` 的 try 只包住 `DoUpdate`

反编译 `Terraria.Main.Update`（`main-decomp.cs:16754–16791`）的结构是：

```
try { DoUpdate(ref gameTime); } catch (Exception) { }   // 空 catch
...
((Game)this).Update(gameTime);                          // ← 在 try 之外
```

而 `FrameworkDispatcher.Update()`（它会触发 `BufferNeeded`）正是由第二行进入的。
结论：**回调里的任何异常都不会被吞，会一路穿到 `Game.Tick`**。这是 R1 定为高风险的依据。

同理，本模组的 `On_Main.Update` 钩子在 `orig(...)` 返回之后执行，也不在 `DoUpdate` 的 catch 内——
现有的 `DefaultAmbientHandler.Update()` 已经是裸奔状态，流式代码必须自带 `try/catch`。

### 1.2 线程亲和性：`Stop/Dispose` 会校验主线程

`ThreadCheck.CheckThread()`（FNA）在非主线程调用时抛
`ThreadStateException("most FNA3D audio/graphics functions must be called on the main thread")`。
`SoundEffectInstance.Stop(bool immediate)` 在 `isDynamic` 分支里调用它（`sei2.cs:295–297`）。

补一条副作用：`CheckThread()` 首次调用时会把**当前线程**记为主线程。所以绝不能让工作线程
抢先触发 `Stop`，否则主线程反而变成“非法线程”。

### 1.3 无设备时抛异常，不是返回 null

`SoundEffect.Device()`（`soundeffect.cs:485`）在 `FAudioContext.Create()` 失败后
抛 `NoAudioHardwareException`，不是返回 null。任何「判空即可」的写法都挡不住。

### 1.4 `Stop` 在 `handle == 0` 时直接返回 → 预填会漏内存

`Stop(immediate)` 首行是 `if (handle == IntPtr.Zero) return;`，而 `ClearBuffers()` 只在
`immediate` 分支里调用（`sei2.cs:285–320`）。`SubmitBuffer` 在 `State == Stopped` 时已经把
原生内存 `Malloc` 出来了（记在 `queuedBuffers`）。

结论：**先预填、后 Play 的顺序本身正确，但一旦 Play 失败（没有 handle），那 3 块原生内存就没人释放**。
预案：`Device()` 探活 → 构造实例 → 预填 → `Play()` 全部放在同一段 `try`，失败即标记本会话关闭流式。

### 1.5 读 `.tmod` 必须用独立句柄

`TmodFile.GetStream(name)`（默认参数）用的是**全局唯一**的 `sharedEntryReadStream`，
第二个并发读取会抛 `Previous entry read stream not closed`。
而 tML 自己的资源源是这么写的（`TModContentSource.OpenStream`）：

```csharp
return file.GetStream(assetName, newFileStream: true);
```

所以 `Mod.GetFileBytes(path)`（内部走共享流）不适合我们；正确写法是
`Mod.GetFileStream(path, true)` 读到 `byte[]`，它每次开独立 `FileStream`，可并发、可 seek。

### 1.6 `.ogg` 不参与压缩

`TmodFile.ShouldCompress` 明确排除 `.png/.mp3/.ogg`，所以取流的字节数 == 磁盘文件大小，
没有解压开销，也没有「解压后再拷一份」的双倍内存。

---

## 2. 三层兜底（从内到外）

```
第 1 层  流内自愈        欠载 → 塞静音并计数；解码异常 → 重开 reader；句柄失效 → 重建一次
第 2 层  单条降级        该条流连续故障 → 回退成常驻 SoundEffect（体积代价 ≤ 23 MB/条）
第 3 层  全局关闭        配置开关 streamingEnabled=false → 完全回到 1.4 的行为
```

设计要点：

1. **故障是粘滞的**。同一条流 10 秒内触发 3 次自愈就升级到第 2 层，避免“每帧重试”把主线程拖死。
2. **第 2 层必须能独立工作**。`StreamedAmbience` 内部持有 `SoundEffectInstance` 作为替补，
   切换时不改公开面，`AmbientHandler` 无感。
3. **第 3 层是给用户的逃生门**。配置项放在 `AmbientConfig`，默认开，出问题一键回到旧行为，
   不用回滚版本。这一条也顺便是灰度手段：先默认关，自己开，稳定后再默认开。

---

## 3. 各风险的落地检查点

| 编号 | 代码里的落点 | 自检方式 |
|---|---|---|
| R1 | `OggStream.OnBufferNeeded` / `Update` 整体 try-catch | 人为在回调里 `throw`，确认只打日志不崩 |
| R2 | 线程模型注释 + `Debug.Assert(ThreadCheck.IsMainThread)`（仅在提交处） | 在解码线程故意调 `Stop()`，确认断言先炸 |
| R3 | `TerrariaAmbience.Unload()`、`AudioLoopsSystem.OnWorldUnload()` | 反复退世界/重载模组，看 `ActiveStreams` 是否归零 |
| R4 | `StreamAssets.Load` 用 `GetFileStream(path, true)` | 并发读 10 个文件，确认无 IOException |
| R5 | `Play()` 的 try 块 | 构造后立刻 Dispose（不 Play），比对进程句柄数 |
| R6 | `StreamPool.Init` 里的设备探活 | 禁用音频设备启动，确认只打一条日志且不崩 |
| R7 | `OggStream.Update` 的 `handle == 0` 重建分支 | 播放中切默认设备，观察是否重建/降级 |
| R8 | `StreamPool.Activate` 的过渡位 | 黎明切曲瞬间抓日志，确认同时活跃 ≤ 5 且旧流在新流预填后才停 |
| R9 | `Update` 的补货逻辑 | 最小化 30 秒再恢复，听是否有咔哒 |
| R10 | 每 tick `ApplyReverb` | 室内站 1 分钟，比对湿声是否存在 |
| R12 | `StreamAssets.Clear()` | 进出世界 5 次，看 `GC.GetTotalMemory` 不单调上涨 |

---

## 4. 验证手段的边界（来自 tModLoader 实机 QA 约束）

这块必须说清楚，否则会误以为「smoke 过了就等于能听」。

| 手段 | 能证明 | 不能证明 |
|---|---|---|
| 离线 PCM 导出（不启动游戏） | 解码正确、循环无缝、无丢帧、耗时达标 | 声卡链路、混响、音量包络 |
| 服务器 smoke | 程序集能加载、`NVorbis` 能解析 | 任何与声音有关的事（服务端没有音频设备） |
| 客户端实机（隐藏桌面 + 私有 savedir） | 不崩、日志指标正常、内存下降 | **听感**（无人值守时没人听） |
| 人工 10 秒 | 听感、爆音、接缝 | 可回归、可量化 |

因此验收拆成两段：

1. **机器判定**：离线导出的 wav 指标（接缝 Δ ≤ P99.5、总样本数无丢帧、欠载计数为 0）＋ 实机日志指标。
2. **人工判定**：用户戴着耳机在雪原/森林/水下各站 1 分钟，确认没有周期性咔哒与断音。

第 1 段我可以独立完成；第 2 段必须由你执行，这是硬约束，不是偷懒。

---

## 5. 明确不做的部分

| 项 | 原因 |
|---|---|
| 修改 `TerrariaAmbienceAPI.dll` | 仓库无工程文件；且有 A1/A2/A6 一堆历史缺陷，改了会牵动其他模组 |
| 迁移脚步 / 开箱 / 合成 / NPC 叫声 | 要求同 tick 出声，且只占约 19 MB |
| 迁移 97 个 wav 一次性音效 | 调用点分散、收益 40 MB，性价比低 |
| 设备热切换的完美恢复 | FNA 层没有重建设备的公开入口，tML 自带音乐同样做不到 |
| 资产重编码（降采样/单声道） | 会改变音色，按原计划不动资产 |

---

## 6. 预案带来的排期变化

原估 15 人日，加上本文的兜底与观测，调整为：

| 项 | 增量 | 说明 |
|---|---|---|
| R1/R2 的防御性封装 | +0.5 天 | 一次性写好，后续省事 |
| 第 2 层降级（常驻替补） | +1 天 | 需要把 `SoundEffect` 路径保留在 `StreamedAmbience` 内 |
| 第 3 层配置开关 | +0.5 天 | 开关 + 本地化条目 |
| 离线 PCM 导出与指标 | +1 天 | 可复用现有 `oggprobe` 的统计代码 |
| 实机观测面板（活跃流/欠载/池状态） | +0.5 天 | 复用 `debugInterface` 面板 |
| **合计** | **+3.5 天** | 现实总量 15 → 18.5 人日（含风险缓冲约 20–22 人日） |

可以延后的部分：R7（设备热切换）与 R13（池容量调优）先按「出问题再动」处理，不占前置工期。
