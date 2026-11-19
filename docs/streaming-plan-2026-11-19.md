# 流式音频落地方案（2026-11-19）

> 目标：把 447 MB 的解码 PCM 常驻内存降下来，同时不引入切换卡顿、破音、崩溃。
> 状态：**计划，未动代码**。本文所有数字为本机实测，所有 API 结论为反编译核实。

---

## 0. 结论先行

**不要"把流式当第一件事做"。** 按收益/风险排序，正确的顺序是：

```
删预加载（省 198 MB，1 小时）  →  懒加载（再省 ~140 MB）  →  流式（再省 ~30 MB + 消除卡顿）
```

流式的真正价值不是省内存（懒加载已经能省掉 90%），而是：

- 消除每次切群系时 10~23 MB 的对象分配与回收（GC 抖动）
- 消除首次播放的解码延迟（`SoundEffect` 构造要把整首解码完才返回）
- 让"25 条音轨常驻"变成"2~3 条活跃"，为以后加内容留出预算

所以建议：**P0~P2 先做（低风险、可独立发布），P3 起才是流式。**

---

## 1. 实测基线

### 1.1 音频构成（372 个文件，65.1 MB 磁盘）

| 组 | 解码后 | 磁盘 | 文件数 |
|---|---:|---:|---:|
| biome 环境床 | **369 MB** | 37.4 MB | 43 |
| 其他（多为 wav 一次性音效） | 40 MB | 22.1 MB | 97 |
| environment（风/水下） | 20 MB | 3.2 MB | 12 |
| footsteps | 14 MB | 1.8 MB | 199 |
| player/chest/craft | 4 MB | 0.4 MB | 15 |
| npcs | 1 MB | 0.1 MB | 6 |
| **合计** | **447 MB** | 65.1 MB | 372 |

格式分布：284 个 `.ogg` + 88 个 `.wav`。解码倍率约 9.5 倍。

> **修正**：此前文档里的"997 MB"是估算值，实测为 **447 MB**。
> tML 日志里的 `Mods using the most RAM: 25.8 MB` 统计的是**包内文件大小**，不是解码内存，不能用来判断这里的问题。

### 1.2 关键结构

- 43 个 biome 文件占了 **82%**，其中森林 24 个文件 **198 MB**。
- 这 43 个都是"长循环 + 逐 tick 音量包络"，**不存在延迟敏感需求**（淡入本来就是几百毫秒）。
- 反过来，脚步（199 个）、开箱、合成这些必须常驻——它们要求同 tick 出声。

### 1.3 一处现成的浪费

`AmbientHandler.InitializeAllAmbienceToAvoidRuntimeOverhead()` 在 `Load()` 阶段无条件
`Request<SoundEffect>` 了**全部 24 首森林曲目**（6 晨 + 6 昼 + 6 暮 + 6 夜）。

一次游戏里最多播 1~2 首，其余 22 首纯粹白占 **约 180 MB**。

---

## 2. 三条路线对比

| | A. 只删预加载 | B. 懒加载 | C. 流式 |
|---|---|---|---|
| 常驻解码内存 | 447 → **249 MB** | 447 → **~105 MB** | 447 → **~78 MB** |
| 切换群系延迟 | 无变化 | 需预热，否则 100~300 ms 静音 | **无**（预填 300 ms 即出声） |
| 新代码量 | ~10 行 | ~200 行 | ~400 行 + 线程/池 |
| 失败模式 | 黎明切换时卡一下 | 预热漏了会静音 | 欠载爆音、循环接缝、设备重置 |
| 可独立发布 | 是 | 是 | 否（需前两步打底） |

**B 和 C 的内存收益只差 27 MB，但 C 的风险高一个量级。** 这就是为什么要把 C 放在最后。

---

## 3. 落地阶段

### P0 · 埋点（半天）

没有基线就没有验收。

- 新增 `Sounds/Diagnostics/AudioStats.cs`（仅 `debugInterface` 开启时生效）
- 每 60 tick 记录：`GC.GetTotalMemory(false)`、`Process.WorkingSet64`、
  活跃流数量、累计欠载次数、单缓冲区解码耗时 P95
- 输出到 `tModLoader-Logs/client.log`，一行一条，便于前后对比

**验收**：森林里站 60 秒，日志能稳定输出上述 5 个数。

---

### P1 · 删掉预加载（1 小时，独立可发布）

**改动**
1. 删除 `InitializeAllAmbienceToAvoidRuntimeOverhead()` 的 4 个循环及其调用点。
2. 改为"临近切换才预热"：`Main.dayTime` 变化前 30 秒（`Main.dayRate` 可推算）异步
   `Request<SoundEffect>(..., AssetRequestMode.AsyncLoad)` 下一时段那一组。

**验收**
- 主菜单进世界后 10 秒，进程内存比改动前低 **≥150 MB**
- 黎明/黄昏切换时无静音（监听 `Volume` 是否在 1 秒内起来）

**回退**：单文件删除，`git revert` 一行。

> 这一步单独就值得做——它不依赖后面任何设计。

---

### P2 · 懒加载 + 引用计数（2~3 天，独立可发布）

**目标**：把"常驻 43 条"变成"常驻当前 2~3 条"。

**新类** `Sounds/Loading/ResidentSound.cs`

```
状态机：NotLoaded → Loading → Ready → Cooling → Disposed
产生：Acquire(path)   引用 +1，未加载则发起 AsyncLoad
消费：Release()       引用 -1；归零后进入 Cooling，10 秒后 Dispose
```

**接入点**
- `ModAmbience` 目前 `SoundInstance { get; private set; }` 由构造时 `Initialize()` 赋值。
  不改 API 的前提下，用一个**平行类** `ResidentAmbience` 承接同样的公开面
  （`Volume / MaxVolume / VolumeStep / Pan / Pitch / PlayInMenu / ChangeTrack`），
  让 `AmbientHandler` 逐字段迁移。
- 迁移顺序：`ForestDay` → `SnowDay` → 其余 biome → environment。
  每迁一个，跑一次 P0 的埋点对比。

**必须处理的三件事**
1. **未就绪不得崩**：`Volume` 写入前判 `State == Ready`。这会把 N1/A4 那类空引用
   从"运行时偶发"变成"编译期不可能"。
2. **切换预热**：`playWhen` 由假变真的那一 tick，立刻 `Acquire`；
   由于有 300 ms 淡入，异步加载通常赶得上。
3. **降温期**：刚淡出的音轨保持 10 秒不释放，避免来回跨群系时反复加载。

**验收**
- 站森林 1 分钟：常驻 `SoundEffect` ≤ 4 个
- 森林→雪原→丛林→森林 往返 5 次：无静音、无卡顿 > 1 帧、内存不单调上涨
- 内存比 P1 再降 **≥120 MB**

---

### P3 · 流式内核（隔离验证，2~3 天）

**关键前提（已核实）**

| 事实 | 结论 |
|---|---|
| `DynamicSoundEffectInstance : SoundEffectInstance` | 可以直接塞进现有包络/混响代码 |
| `IsLooped` 的 setter 是**空实现** | 循环必须自己做（重填缓冲），不能设标志 |
| `FrameworkDispatcher.Update()` 在 `Game.Tick` 里被调用（2 处） | `BufferNeeded` 会自动触发，无需手动泵 |
| `Update()` 在 `PendingBufferCount < 3` 时调 `BufferNeeded` 恰好 `3 - Pending` 次 | 目标是**恒定维持 3 个在飞缓冲** |
| `ApplyCustomReverb` 用 `inst.handle` + `inst.dspSettings` | 动态实例同样有这两个成员，**混响零改动可用** |
| `Pitch` setter 调 `UpdatePitch()` 作用于 handle | 水下变调同样可用 |
| `NVorbis 0.10.0.3` 在 `tModLoader.deps.json` 里 | **不需要自带 DLL**，直接 `using NVorbis` |
| `.tmod` 是 deflate 容器，内部条目不可 seek | 每条流必须把压缩后的 ogg（≤2.2 MB）读进 `MemoryStream` |

**新类** `Sounds/Streaming/OggStream.cs`

```
字段：VorbisReader(reader, MemoryStream) / DynamicSoundEffectInstance(rate, Stereo)
      float[] _decodeBuf / byte[] _pcmBuf × 3 / ConcurrentQueue<byte[]> _ready
      Thread _producer / CancellationTokenSource / int _underruns
```

**缓冲区规格**
- 每块 100 ms：44.1 kHz 下 4410 帧 × 2ch × 2B = **17,640 B**
- 3 块在飞 = 52.9 KB/流（对比常驻一条 10~23 MB）

**线程规则（最重要）**
- 解码在**工作线程**；`SubmitBuffer` **只在主线程**调用。
  理由：FNA 的 `PendingBufferCount` 直接读 `List.Count`，无锁；跨线程 Submit 会破掉这个假设。
- `BufferNeeded` 主线程回调：从 `_ready` 取一块提交；取不到则提交静音并 `_underruns++`。
- 生产者只在 `_ready.Count < 3` 时补货，避免无限堆积。

**单声道处理（这是 API 版 A1 崩溃的根因）**
- 实例固定 `AudioChannels.Stereo` 创建。
- 解码用 `reader.ReadSamples(buf, 0, count, 2)` 让 NVorbis 做上混；
  若走手工转换，必须按 `frames × 目标声道数` 计算写入长度，禁止按源声道数写。
- 每条流创建时校验：`_pcmBuf.Length % 4 == 0`。

**无缝循环**
- 读到 `TotalSamples` 时不 `Stop()`，而是 `reader.SamplePosition = 0` 继续填**同一块**。
- 接缝处加 64 帧等功率交叉淡化做保险（防止 ogg 本身首尾不严丝合缝）。

**启动/停止**
- `Play()` 前先填满 3 块，再从 `Volume = 0` 起淡入 200 ms。
- 停止顺序：取消 token → `Join(500)` → `Stop()` → `Dispose()` → `ClearBuffers`。
- 任何解码异常在生产者内部捕获，标记 `Faulted`，`LogOnce`，**绝不外抛**到 `On_Main.Update`。

**离线验证（不启动游戏）**
- 写一个 `#if DEBUG` 的控制台入口，用同一套代码解码 2.2 圈写到 wav；
- 检查：接缝处 `max|Δsample|` ≤ 全文件 `|Δsample|` 的 P99.5；
- 检查：输出总样本数 == 2 × `TotalSamples`（无丢帧）。

**验收**：离线通过 + 单条音轨接入游戏后，10 分钟内 `_underruns == 0`。

---

### P4 · 流池与生命周期（2 天）

25 条音轨不能开 25 条流。

**新类** `Sounds/Streaming/StreamPool.cs`
- 上限 **4**（转场时新旧各一，留 2 条余量）
- `Activate(track)`：池满时回收"`Volume` 持续为 0 最久"的那条
- `Deactivate(track)`：`Volume <= 0.001` 持续 2 秒后释放
- 播放位置记录在 `track` 上，回收后重建可从原位置续（可选）

**接入**：`StreamedAmbience` 实现与 `ResidentAmbience` 相同的公开面，
`AmbientHandler.Ambiences` 允许混装两种类型。

**验收**
- 同时活跃流 ≤ 4（日志断言）
- 快速穿越 6 个群系：无欠载、无爆音、流数量正确回落

---

### P5 · 全量迁移与回归（2 天）

- 43 条 biome + 12 条 environment（风、水下、雨）迁到流式
- 199 个脚步 + 开箱/合成/NPC 保持常驻（延迟敏感，且只占 19 MB）
- 97 个 wav 一次性音效保持现状（总计 40 MB，收益低、调用点分散）

**最终内存预期**

| 阶段 | 常驻解码 |
|---|---:|
| 现在 | 447 MB |
| P1 后 | ~249 MB |
| P2 后 | ~105 MB |
| P5 后 | **~78 MB**（+ 活跃流 ~0.5 MB） |

---

## 4. 风险与对策

| 风险 | 触发条件 | 对策 |
|---|---|---|
| 欠载爆音 | 主线程卡顿 > 300 ms | 3 块缓冲；欠载时提交静音而非等待；计数告警 |
| 循环接缝可闻 | ogg 首尾不连续 | 读取时回绕而非重启；64 帧交叉淡化 |
| 设备切换后失声 | 换耳机/默认设备 | 每 tick 检 `IsDisposed`，重建流并从当前位置续 |
| 暂停/最小化后失声 | `Game.Tick` 停止 | 记录期望状态，恢复时重新预填 |
| 混响失效 | `dspSettings` 未初始化 | 构造后断言 `dspSettings.SrcChannelCount == 2` |
| 内存不降反升 | `MemoryStream` 未随流释放 | 流 Dispose 时显式置空并 `GC` 一次（仅调试期） |
| 与 API 版冲突 | 同时存在 `ModStreamedAmbience` | 不引用 API 的流式类，全部用自研 |

---

## 5. 验证矩阵

| 维度 | 方法 | 通过标准 |
|---|---|---|
| 内存 | P0 埋点，4 个检查点（主菜单/森林/10 分钟/群系巡游后） | 达标见 §3 P5 表 |
| 欠载 | 计数器 | 10 分钟 0 次 |
| 循环 | 离线 wav 接缝分析 | 接缝 Δ ≤ P99.5 |
| CPU | 单块解码耗时 | P95 < 1 ms（约单核 1%） |
| 转场 | 森林→雪原→丛林→森林 ×5 | 无静音、无 > 1 帧卡顿 |
| 混响/变调 | 下水，比对 `Pitch` 与低通 | 与改动前听感一致 |
| 长稳 | 连续 30 分钟 | 流数量、句柄数、内存均无单调上涨 |

---

## 6. 需要你定的三件事

1. **做到哪一步**：只做 P1（1 小时，省 198 MB），还是做到 P2（省 342 MB），还是全做（省 369 MB + 消除卡顿）？
2. **能不能接受资产改动**：把 43 条 biome 重编码为 22.05 kHz 或单声道，可再省一半，但会改变音色——
   我默认**不动资产**。
3. **`ModAmbience` 要不要内联**：P2 用平行类可以绕开；但长期看内联（约 350 行，
   按行为重写而非照搬反编译）能一并解决 API 侧 A3/A5/A6 三个遗留问题。
   建议放到 P5 之后单独做。

---

## 附：为什么不用 API 现成的 `ModStreamedAmbience`

| 问题 | 后果 |
|---|---|
| 单声道 Ogg 缓冲越界（写 `dst[4095]` 后二次越界） | 直接抛异常，穿过 `On_Main.Update` |
| `UpdateAll` 里裸 `Main.NewText` | 每条流每帧刷一条聊天消息 |
| 静态 `_playPosition` 跨实例共享 | 两条流互相踩位置 |
| `Mod.Call` 无对应分支 | 只能反射或直接构造，无契约 |
| `StreamedAmbienceManager` 全是死代码 | 无池化、无回收 |

自研 400 行比修它更省事，且不引入对第三方程序集的又一层依赖。
