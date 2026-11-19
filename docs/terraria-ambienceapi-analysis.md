# TerrariaAmbienceAPI 拆解分析

- **分析对象**：`TerrariaAmbienceAPI` v1.3.6.8（Workshop ID `2826615136`，作者 RighteousRyan）
- **分析日期**：2026-11-18
- **方法**：程序集反编译（ilspycmd 9.1）+ `.tmod` 容器格式解析 + 服务器运行日志交叉验证
- **证据目录**：`.home/qa/api-decomp/`（反编译源码）、`.home/qa/apiver/`（4 个历史版本 DLL）

---

## 1. 结论

**它是一个独立的"音频基元库"模组，不是 TerrariaAmbience 的内部模块。** 主模组的环境音与脚步声系统完全建立其上，但在实现上绕过了它设计的 `Mod.Call` 入口，改为直接引用类型——因此这是**编译期硬依赖**，而非松耦合的 API 调用。

三个核心事实：

1. **主模组的 25 个氛围音轨全部是 `ModAmbience` 实例**，24 组脚步声全部是 `FootstepSound` 实例。移除该 API 等于移除主模组的音频层。
2. **仓库 `lib/TerrariaAmbienceAPI.dll` 与工作坊 2025.10 版本字节完全一致**（SHA-256 `fe55f4f1bd18d437…`，29,696 B）。当前不存在版本漂移，但这也意味着 API 的任何更新都会直接改变主模组的运行行为，没有任何缓冲层。
3. **API 侧存在若干可证的缺陷**，其中至少一项会经由主模组代码路径直接暴露（`ChangeTrack` 与 `SoundInstance` 的空引用假设，对应 QA 台账 N1 的根因）。

---

## 2. 身份与版本谱系

### 2.1 来源

| 项 | 值 |
|---|---|
| 显示名 | Terraria Ambience API |
| 内部名 | `TerrariaAmbienceAPI` |
| Workshop | `2826615136` |
| 作者 | RighteousRyan |
| 文档 | `https://github.com/RighteousRyan1/TerrariaAmbienceAPI/wiki` |
| 命名空间 | `TerrariaAmbienceAPI` / `.Common` / `.Common.Utilities` / `.Content` |

### 2.2 四个历史版本

从 `.tmod` 容器头解析出的真实元数据（版本号 ≠ 目录名，目录名是构建日期）：

| 目录 | mod 版本 | 构建用 tML | DLL 大小 | 类型增量 |
|---|---|---|---|---|
| `2022.5` | **1.2** | 2022.5.103.34 | 15,360 B | 基线 |
| `2025.2` | **1.3.6.6** | 2025.2.3.3 | 20,480 B | `+ PlayerConfig` |
| `2025.6` | **1.3.6.7** | 2025.6.3.0 | 26,624 B | `+ ModStreamedAmbience`、`+ StreamedAmbienceManager` |
| `2025.10` | **1.3.6.8** | 2025.10.3.1 | 29,696 B | 与 `lib/` 字节一致 |

### 2.3 关键 API 断代（1.2 → 1.3）

`ModAmbience` 的播放条件签名发生过一次破坏性变更：

```csharp
// v1.2
public ModAmbience(Mod mod, string path, string name, float maxVolume, float volumeStep, Func<bool> playWhen)
public bool WhenToPlay { get; set; }   // 由 API 每 tick 写入

// v1.3.6.8
public ModAmbience(Mod mod, string path, string name, float maxVolume, float volumeStep, Func<ModAmbience, bool> playWhen)
// WhenToPlay 已删除，改为只读的 AreConditionsMet
```

主模组使用的是 v1.3 签名（`(a) => ...` 带参数），因此**不能降级到 1.2**。

---

## 3. 它到底提供了什么

### 3.1 `ModAmbience` —— 循环氛围床

一个 `ModAmbience` = 一条常驻循环音轨 + 一个逐 tick 的音量包络。

```csharp
// ModAmbience.cs:106-132（语义还原）
internal void INTERNAL_Update() {
    if (!PlayInMenu && Main.gameMenu) { Volume = 0f; return; }   // 注意：未写回 SoundInstance
    AreConditionsMet = playWhen(this);
    if (SoundInstance == null) return;

    if (AreConditionsMet && Main.hasFocus) {
        Volume += VolumeStep;
        if (Volume > MaxVolume) Volume = MaxVolume;              // 上限只约束上升
    }
    if (!AreConditionsMet || (!Main.hasFocus && fadeOnFocusLost) || Main.gameMenu)
        Volume -= VolumeStep;

    Volume = Clamp(Volume, 0f, 1f);
    SoundInstance.Volume = Volume * Main.ambientVolume;          // 乘全局环境音量
}
```

设计要点：

- **构造即注册**：构造函数把自身加入 `TerrariaAmbienceAPI.AllModAmbiences`，并由 `autoInit` 触发 `Initialize()`。
- **`Initialize()` 在服务端直接返回**（`ModAmbience.cs:83-86`），因此服务端实例的 `SoundInstance` 恒为 `null`。
- **`IsPlaying` 是 `Volume > 0f`**（`:28`），语义是"应该听得见"，不是 `SoundState.Playing`。
- **常驻而非按需**：`Sound` 以 `AssetRequestMode.ImmediateLoad` 真实加载，14~25 条音轨在模组加载阶段一次性解压进内存。这是服务端也占用 25.9 MB 音频资源的直接原因。
- **换曲**：`ChangeTrack(path)` 停止旧实例、重新请求资源、重建实例并播放。主模组用它在昼夜切换时换森林音轨。

**这条路子适合"永远存在、只调音量"的长音轨，不适合按需播放的一次性音效。** 主模组的 25 条环境音全部符合这个前提。

### 3.2 `FootstepSound` —— 脚步变体

一条 `FootstepSound` = 一组同目录的顺序编号音频变体 + 触发条件。

```csharp
// FootstepSound.cs:131-189（语义还原）
bool isStep  = (legFrameNew == 44 && legFrameOld != 44) || (legFrameNew == 25 && legFrameOld != 25);
bool isLand  = legFrameNew != 14 && legFrameOld == 14;
if ((!isStep && !isLand) || !PlayerTileChecker.ShouldStep || player.velocity.Y != 0f || player.pulley)
    return;

// 步声音量 = StepVolume × VolumeMultiplier × clamp(|vx|/5, 0, 1)
// 落地音量 = LandVolume × VolumeMultiplier × clamp((Bottom.Y - fallStart*16)/200, 0, 1)
```

触发源是 API 自带的 `FootstepSoundPlayer : ModPlayer`，它把 `legFrame.Y / 20` 与上一 tick 比较，识别迈步帧（44 / 25）与落地帧（14）。

**脚下瓦片从哪来**：`PlayerTileChecker : GlobalTile`，在 `FloorVisuals(type, player)` 里写入 `static int TileId`。这是整个脚步声系统的唯一数据源。

**变体解析的实际行为（`GetAllVariants`，`:111-129`）**：

```csharp
var files = Mod.GetFileNames().Where(f => GetDirectoryName(f) == dirToUse).OrderBy(f => f).ToArray();
for (int num = 0; num < VariantCount; num++) {
    string path = array[num];                                    // 只用来取目录
    string text = Mod.Name + "\\" + GetDirectoryName(path) + "\\" + fileNameWithoutExtension + (num + 1);
    list.Add(new SoundStyle(text.Replace('\\','/'), SoundType.Sound));
}
```

即：**路径完全由 `baseFileName + (n+1)` 拼出，`array[num]` 只贡献目录名。** 那行 `OrderBy` + 索引实际上是"目录文件数必须 ≥ VariantCount"的隐式断言——不足时抛 `IndexOutOfRangeException`，在模组加载阶段炸掉。

已核验主模组 24 组声明与实际文件数：**全部满足**（唯一"异常"是 `armor/` 目录同时存放 `heavy1-9` 与 `light1-6`，共 15 个文件，容量足够，路径拼接仍正确）。属**脆弱但当前正确**。

### 3.3 `ModStreamedAmbience` —— Ogg 流式播放（未完成）

1.3.6.7 新增，用 NVorbis 解码 + `DynamicSoundEffectInstance` 分块提交，支持长音轨不进内存。

- 上限固定 4 条并发（`MaxActiveStreams = 4`，`:14`）
- 4 个待处理缓冲 × 1024 帧 × 2 声道 × 2 字节 ≈ 16 KB/条
- 支持"跨流断开续播"：用静态 `_globalLastPositionSamples` 记录位置

**但它没有被接入对外入口。** `Mod.Call` 只分发 `"Ambience"` 与 `"Footstep"` 两个字符串（`TerrariaAmbienceAPI.cs:36-49`），没有 `"Streamed"` 分支；也没有工厂方法。外部模组只能用 `new ModStreamedAmbience(...)`，即必须直接引用程序集。

**主模组完全不使用它**（全仓库零引用）。

---

## 4. 与主模组的真实耦合

主模组没有走 `Mod.Call`，而是直接 `new`：

```csharp
// Content/AmbientAndMore/AmbientHandler.cs:97
BreezeLight = new(mod, AmbientPath + "environment/breeze_light", "BreezeLight",
                  maxVolume: 1f, volumeStep: TransitionHarshness, (a) => IsStrictlySurface);
```

| 主模组位置 | 使用的 API 成员 | 性质 |
|---|---|---|
| `AmbientHandler.cs:28-64` | `ModAmbience` 字段 × 25 | 数据 |
| `AmbientHandler.cs:97-190` | `new ModAmbience(...)` × 25 | 构造 |
| `AmbientHandler.cs:282-290` | `PlayInMenu`、`MaxVolume`、`VolumeStep`、`SoundInstance.Pitch` | 运行时调参 |
| `AmbientHandler.cs:226-266`、`TerrariaAmbience.cs:277-354` | `ChangeTrack(...)` | 昼夜换曲 |
| `FootstepHandler.cs:98-160` | `new FootstepSound(...)` × 24、`UseAnyTile`、`FootstepConditions`、`VolumeMultiplier` | 构造 |
| `Core/MethodDetours.cs` | `PlayerTileChecker` | 读静态瓦片 |

**结论**：主模组是"API 的第一个客户"，且事实上绑定在某一具体构建上。API 的缺陷会以主模组缺陷的形式出现，反之亦然。

---

## 5. 缺陷清单

### A1 · `ModStreamedAmbience` 对单声道 Ogg 必然溢出（P1，静态可证）

**证据**：`ModStreamedAmbience.cs:361-391` + `:393-452`

```
_floatBuffer 长度 = 1024 × (立体声输出 ? 2 : 1) = 2048
ReadInterleaved(dst) 对单声道源读 dst.Length = 2048 个样本 → num = 2048
mono→stereo 分支：num3 = num*2-1 = 4095，然后 dst[4095] = ...   ← 越界（数组长 2048）
返回 num2*2 = 4096 → FillAndSubmitBuffer 再按 4096 写入 4096 字节的 _pcmBuffer ← 二度越界
```

**影响**：任何单声道 `.ogg` 一旦开始流式播放，立即 `IndexOutOfRangeException`；`TryStartStreaming` 捕获后 `throw;` 原样上抛，穿过 `UpdateAll` → `On_Main.Update` 钩子 → 游戏崩溃。

**触发条件**：使用 `ModStreamedAmbience` + 单声道资源。**主模组不触发**（未使用该类），属 API 的潜伏缺陷。

**建议验证**：造一个单声道 ogg，`new ModStreamedAmbience(...)` 后把 Volume 抬到 >0 观察日志。

---

### A2 · 流式更新每 tick 向聊天框刷一条消息（P1，潜伏）

**证据**：`ModStreamedAmbience.cs:120-137`

```csharp
if (item.AreConditionsMet) {
    item.Volume += item.VolumeStep;
    if (item.Volume != 0f)
        Main.NewText($"{item.Name}: {item.Volume}", byte.MaxValue, byte.MaxValue, byte.MaxValue);
}
```

**影响**：只要有一条流式音轨在淡入/淡出，`Volume` 几乎恒 ≠ 0，于是**每帧一条**——60 条/秒/音轨，最多 240 条/秒。这是明显的调试残留，无任何配置开关。

**备注**：该缺陷在 1.3.6.7 与 1.3.6.8 中完全相同，说明 1.3.6.8 未清理。

---

### A3 · 配置重试的 Stopwatch 从不 `Reset()`（P2，条件触发）

**证据**：`TerrariaAmbienceAPI.cs:79-87, 95-115`

```csharp
if (stopwatch.Elapsed.TotalSeconds > 5.0) { stopwatch.Stop(); TryPopulateConfig(); }
...
catch { stopwatch.Start(); /* 没有 Reset() */ }
```

**影响**：`Stop()` 不会归零。一旦 `TryPopulateConfig` 失败过一次，`Elapsed` 永久 ≥ 5 秒，此后**每帧**都会重新执行 `TryPopulateConfig`——每帧 `new Dictionary<string,float>()` 并重建全部键值，把用户改过的 `footstepVolumes` 反复清零。

**触发条件**：`AllModAmbiences[i].Mod` 为 null、或 `PlayerConfig` 尚未就绪等。概率不高，但一旦命中就是持续性的配置覆盖 + 每帧分配。

**修复方向**：`catch { stopwatch.Restart(); }`，并在成功分支 `stopwatch.Reset()`。

---

### A4 · `ChangeTrack` / `Initialize` 假定 `SoundInstance` 非空（P1，主模组已受影响）

**证据**：`ModAmbience.cs:56-77`

```csharp
public void ChangeTrack(string path) {
    if (!Main.dedServ) {
        SoundInstance.Stop();                 // ← 无判空
        ...
        SoundInstance = Sound.Value.CreateInstance();   // ← Sound 可能为 null
```

`Initialize()`（`:79-104`）同样在 `CreateInstance()` 可能返回 `null` 的情况下，下一行直接 `SoundInstance.Volume = 0f`。

**影响**：资源缺失 / 路径拼错 / 服务端路径下调用 `ChangeTrack`，都会抛 `NullReferenceException`。主模组在 `AmbientHandler.cs:286` 的 `amb.SoundInstance.Pitch = 0f` 与 `TerrariaAmbience.cs` 的 `ChangeTrack` 调用点构成同样的风险面——**这正是 QA 台账 N1 的 API 侧根因**。

**修复方向**：把"资源就绪"建模成显式状态（`bool IsReady`），所有对 `SoundInstance` 的访问走同一个守卫。

---

### A5 · 主菜单静音只改字段，不写回实例（P2）

**证据**：`ModAmbience.cs:108-112`

```csharp
if (!PlayInMenu && Main.gameMenu) { Volume = 0f; return; }
```

**影响**：`SoundInstance.Volume` 停留在离开游戏前的最后一帧值。若音效实例未随场景切换被停止，主菜单会继续听到上一场景的氛围音。

主模组为此写了规避代码（`AmbientHandler.cs:281-284`：进菜单时设 `PlayInMenu = true` + `MaxVolume = 0f`），说明作者也观察到过这个现象。**这是对 API 缺陷的补丁，不是设计。**

---

### A6 · `CachedAmbiences` 不随卸载清理（P2）

**证据**：`ModAmbience.cs:14, 62, 90`

- `Initialize()` 无条件 `CachedAmbiences.Add(_path, Sound)`，重复键抛 `ArgumentException`（会被 `Call` 的 catch 吞成 "Mod.Call Failed"）
- `Unload()`（`TerrariaAmbienceAPI.cs:117-134`）只清 `AllModAmbiences`，**不清 `CachedAmbiences`**；同一处还 `Dispose()` 了 `Asset<SoundEffect>` 的底层值

**两个具体后果**：

1. **跨模组路径碰撞**。`_path` 是模组内相对路径（如 `Sounds/Custom/ambient/biome/forest/day_1`），**不含模组名**。两个模组若使用同名相对路径，后加载者的 `Add` 直接抛异常，整个 `Call` 失败——报错信息只有 "Mod.Call Failed"，没有任何路径线索。
2. **重载残留**。`Unload` 未清缓存却释放了资产，缓存里留下已释放句柄，`ChangeTrack` 命中时会抛 `ObjectDisposedException`。

**范围限定**：tML 的整包重载会连 API 程序集一起卸载，静态字段随之重置，因此后果 2 只在"API 程序集未被卸载、但内容被重新释放"的边界场景出现。后果 1 不受此限制，是常态风险。

---

### A7 · 调试叠层在关闭状态下仍每帧执行全部计算（P2）

**证据**：`TerrariaAmbienceAPI.cs:136-190`

`DrawString` 被 `debug_ViewAllLoadedModAmbiences` 守卫，但**字符串拼接、`MeasureString`、`spriteBatch.Begin/End` 都不在守卫内**：

```csharp
foreach (var a in AllModAmbiences) {
    text += $"\n{a.Name} ({name})";                 // 每帧字符串拼接
    num += FontAssets.MouseText.Value.MeasureString(text).Y;   // 对递增串反复测量 → O(n²)
}
```

**影响**：默认配置下（两个开关皆 false）仍然每帧对 25 条音轨做 O(n²) 文本测量 + 一次多余的 `Begin/End` 批次。属于纯粹的无用开销，且 `Begin()` 在已有批次打开时会抛异常。

**修复方向**：整段包进 `if (config.debug_...)`，文本缓存到脏标记。

---

### A8 · `StreamedAmbienceManager` 是完全的死代码（P3）

**证据**：`Common/Utilities/StreamedAmbienceManager.cs` 全文

`Register` / `Unregister` / `UpdateActiveStreams` **在整个程序集中零调用点**（已 grep 确认）。它维护的 `activeStreams` 列表与 `ModStreamedAmbience._active` 是两套互不相干的"最多 4 条"记录，实际上只有后者生效。

**影响**：纯维护负担 + 误导性 API 面（`public static void UpdateActiveStreams()` 看起来像入口，调了没用）。

---

### A9 · 跨实例共享的静态播放位置（P3）

**证据**：`ModStreamedAmbience.cs:20-22`

`_globalLastPositionSamples` / `_globalPrevTotalSamples` 是 `static`，但语义是"我这条音轨上次播到哪"。

**影响**：两条流式音轨同时存在时，A 的暂停位置会被 B 写入，恢复位置错乱。字段名带 `_global` 说明作者知道它是全局的，但用法与语义不符。

---

### A10 · `Mod.Call` 的入参读取在 try 之外（P3）

**证据**：`TerrariaAmbienceAPI.cs:27-31`

```csharp
string text = args[0] as string;
object obj = args[1];        // ← 数组长度不足时在这里抛，尚未进入 try
Mod val = (Mod)((obj is Mod) ? obj : null);
string text2 = args[2] as string;
try { ... }
```

**影响**：`Mod.Call("Ambience", this)` 这类少参调用会抛**未捕获**的 `IndexOutOfRangeException`，直接冒泡到调用方模组的加载流程。此外 `args[4] as int?` 对 `byte`/`long` 装箱值返回 `null`，随后 `.Value` 抛 `InvalidOperationException` 被统一吞成 "Mod.Call Failed"，丢失了原始错误信息。

---

### A11 · XML 文档严重过期（P3，文档债）

**证据**：`TerrariaAmbienceAPI.xml`（容器内 6,638 B，自 2022.5 起未变）

文档里列出的成员中，**6 个在当前程序集已不存在**：

| 文档声称 | 现状 |
|---|---|
| `ModAmbience.mod`（小写） | 实际是 `Mod` |
| `WhenToPlay` | v1.3 已删除 |
| `SaveAndQuit()` | 不存在 |
| `UpdateActive()` | 不存在 |
| `UpdateThisAmbience()` | 实际是 `INTERNAL_Update()` |
| `ClampVolume()` | 不存在 |

文档还在说"When inheriting from this class, you MUST use this.Sound and this.SoundInstance"——**继承式用法在 v1.2 就已废弃**，现在是构造式。任何按 XML 文档对接的第三方模组都会撞墙。

---

### A12 · `PlayerTileChecker` 的静态状态无法隔离多玩家（P3，需实测）

**证据**：`Common/PlayerTileChecker.cs:6-18`

`TileId` 是 `static`，由 `GlobalTile.FloorVisuals` 为**每个**玩家写入。当前之所以通常正确，是因为主循环按玩家逐个执行 `Update → PostUpdate`，`FloorVisuals` 与随后的脚步播放落在同一个玩家区间内。

**风险**：这个正确性依赖 tick 内的调用顺序，而非任何显式约束。任何执行顺序调整、或某玩家该 tick 未触发 `FloorVisuals`（例如静止后重新起步的第一帧），都会读到另一玩家留下的瓦片 ID。

**建议**：改为按 `player.whoAmI` 索引的实例状态；验证方式是双人联机、两人分别站在不同材质上同时行走，比对音色。

---

## 6. 对本次 QA 台账的补充

| 台账条目 | 本报告给出的补充 |
|---|---|
| **N1** `AmbientHandler.cs:286` 空引用 | 根因在 API：`ChangeTrack` 与 `Initialize` 都把 `SoundInstance` 当必非空（A4）。主模组只是复刻了同样的假设。 |
| **B23** 服务端缺 `dedServ` 守卫 | 与 API 行为一致：`ModAmbience.Initialize` 在服务端直接返回（`ModAmbience.cs:83`），实例的 `SoundInstance` 恒为 `null`。服务端"空对象图"不是主模组独有，而是 API 的既定语义。 |
| `Loaded 0 ModAmbience(s)` | 准确解释：`AmbientHandler.Initialize()` 在 `Main.dedServ` 时提前 return，因此服务端根本没有构造 `ModAmbience`；API 的 `PostAddRecipes` 如实报告 0。 |

**附带确认**：`PostAddRecipes` 虽然在 tML 2026.9.3.1 中被标记 `[Obsolete]`，但**仍会被调用**——服务器日志 `23:11:43.697 [TerrariaAmbienceAPI]: Loaded 0 ModAmbience(s).` 即其输出。

---

## 7. 若要摆脱这个依赖

| 方案 | 做法 | 成本 | 风险 |
|---|---|---|---|
| **A. 复制源码内联** | 把 `ModAmbience` / `FootstepSound` / `PlayerTileChecker` 三个类搬进主模组，删掉程序集引用 | 中（约 350 行，无外部依赖，仅 XNA + tML） | 低。可顺带修掉 A4~A7；但需自行维护 API 的后续兼容性 |
| **B. 加薄包装层** | 保留引用，在主模组内建 `AmbienceHandle` 包装，集中判空与生命周期 | 低 | 只隔离空引用，不解决版本耦合 |
| **C. 完全重写** | 用 `SoundEngine.PlaySound` + 自管 `SoundEffectInstance` 重做 | 高（需重做淡入淡出、换曲、脚步变体解析） | 高。当前系统已调好听感，重写会回归 |

**建议 A**，理由：这三个类的实现总量小、无隐藏依赖，且当前 API 版本自 2025.10 后未再更新（工作坊最新即 1.3.6.8）。内联后可一并消除 A4（空引用）、A5（菜单静音）、A7（每帧文本测量）三项，同时让 `ModAmbience` 的 25 条音轨改为按需加载，直接缓解服务端 25.9 MB 资源占用。

---

## 8. 复现方式

```bash
# 1) 反编译（需 dotnet tool install --global ilspycmd）
ilspycmd -p -o api-decomp --nested-directories lib/TerrariaAmbienceAPI.dll

# 2) 解析 .tmod 容器（TMOD + 7-bit 字符串 + 280B 保留区 + 文件表 + deflate 数据区）
# 容器解析脚本见 .home/qa/（本轮已就地执行，未落库）
```

`.tmod` 布局（依据 `Terraria.ModLoader.Core.TmodFile.Save` 反编译）：

```
"TMOD" | byte fmtVer | string tMLVersion | byte[280] 保留区
       | string modName | string modVersion | int32 文件数
       | { string path, int32 length, int32 compressedLength } × N
       | 已缓存文件原始字节 | byte[20] 哈希 | int32 数据区起始偏移
```

---

## 附录 · 公开符号表（v1.3.6.8）

```
TerrariaAmbienceAPI
├─ Mod: TerrariaAmbienceAPI
│   ├─ static List<ModAmbience> AllModAmbiences
│   ├─ Call(params object[])                    // 仅 "Ambience" / "Footstep"
│   ├─ PostSetupContent / PostAddRecipes / Unload
│   └─ (detours) On_Main.Update ×2, On_Main.DrawInterface ×1
├─ ModPlayer: FootstepSoundPlayer               // 迈步/落地帧检测
├─ GlobalTile: PlayerTileChecker                // static TileId / ShouldStep
├─ Common
│   ├─ ModAmbience : IApiContent                // 循环氛围床
│   ├─ ModStreamedAmbience : IDisposable        // Ogg 流式（未接入 Call）
│   ├─ FootstepSound : IApiContent              // 脚步变体
│   ├─ IApiContent, LoadException
│   └─ Utilities: ArrayHelper / CommonUtils / MathMethods / OOPHelper / StreamedAmbienceManager
└─ Content
    ├─ PlayerConfig  (ConfigScope.Client)       // footstepVolumes, fadeOnFocusLost
    └─ DebugConfig   (ConfigScope.Client)       // 调试叠层开关
```
