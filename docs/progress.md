# Terraria Ambience · 进度与缺陷台账

> **这份文件是什么**：本会话把 TerrariaAmbience 的缺陷一条条落到「文件:行号 + 原文 + 后果 + 修法」的记录。它不替代 issue；**§1–§9 是 2026-11-18 那次只读审计的结论，不代表代码已被修改**。`stream-1.4` 分支上的实际改动、验收数据与残留问题集中在 §11。
>
> **怎么读**：先看 §3 总表挑活，再看 §4 里对应 ID 的明细。§5 是上一轮结论的复核（有 2 处更正），§6 是「跟这个 mod 无关」的排除结论，§7 是只能实机验证的部分。

| 项 | 值 |
|---|---|
| 仓库（本 fork） | https://github.com/ora233-fantaea/TerrariaAmbience-fantasea |
| 上游 | https://github.com/RighteousRyan1/TerrariaAmbience |
| 分支 | `1.4`（稳定） · `stream-1.4`（流式改造，2026-11-21） |
| HEAD | `1.4` = `27bd268` · `stream-1.4` = `86c6f99`（2026-11-21 19:20） |
| `build.txt` 版本 | `0.3.7.1`（与仓库 tag 无关：tag 只到 `v0.1.6.x`） |
| 克隆方式 | `git clone` 全量，433 个 tracked 文件，227.8 MB，`git fsck` 无错，工作区干净 |
| 审计日期 | 2026-11-18 |
| 审计方式 | HEAD 源码通读 + 与 0.3.7.1 发布包反编译源码对照 + 一次并行只读审计交叉验证 |

---

## 1. 基线：这份代码和你装的 0.3.7.1 是什么关系

- **版本号相同，代码不同**。你装的那个包构建于 2026-04-03，HEAD 的 `build.txt` 仍写 `0.3.7.1`，但代码已经往后推了五个月（变量名、结构、`SoundFilterSystem` 的实现都改过）。所以：仓库里没有任何 tag 能对上你装的版本，**唯一可用的基线就是 `1.4` HEAD**。
- 上游 `1.4.5` 分支（`34cb1eb`，2026-09-02）比 `1.4` tip 晚 4 小时，是「Port to 1.4.5」；`streamed-ambience` 停在 2025-08-15，没有在进行中的流式改造。本 fork 与上游 `1.4` 完全一致（0 分歧），所以 PR 的 base 天然就是最新。

### 构建前置（下次开工先看这段）

1. `TerrariaAmbience.csproj` 第一行 `<Import Project="..\tModLoader.targets" />` → **这份源码必须放在 `…\tModLoader\ModSources\<某个目录>` 里才能构建**，放别处会找不到 targets。
2. `TargetFramework=net8.0`，`AllowUnsafeBlocks=true`。
3. 用了 `Krafs.Publicizer` 公开 `FNA` 的内部成员（`SoundEffectInstance.handle`、`SoundEffect.Device()`、`device.ReverbVoice`、`device.reverbSends` 等就是这么来的）。**换 tML/FNA 版本时这套后门最容易先炸。**
4. 依赖 `lib\TerrariaAmbienceAPI.dll`（仓库内已带），`build.txt` 里同时写了 `modReferences = TerrariaAmbienceAPI` → 编译靠 dll，运行靠装好那个 API mod。
5. `includePDB = true`（发布包自带 PDB，这也是当初能顺利反编译的原因之一）。

---

## 2. 本次会话做了什么

1. 克隆并校验 fork（见上表）。
2. 与反编译出来的 0.3.7.1 发布包对照：387 个条目 / 372 个音频 / 65.4 MB；tML 按 `duration × 44100 × 4` 计入解码内存 ≈ **997 MB**——这是「装了这个 mod 内存高」的主因，**不是 bug，也不会被本台账里的任何一条修掉**。
3. 逐文件通读 HEAD，重点 20+ 个文件（含把 `TerrariaAmbienceAPI` 之外的调用链全部追到底）；把上一轮的 7 条结论逐条复核，其中 **2 条需要更正**（§5）。
4. 新发现 20+ 条确定缺陷，按「会不会随游玩时长恶化」排序（§3）。
5. 明确排除：本次会话最初怀疑的「下雨卡死」不是这个 mod 引起的（§6）。

---

## 3. 缺陷总表

严重度按「玩家能不能感知 + 是否随时间恶化」给，不按代码丑不丑给。
**确定性**：`确认` = 读代码即可判定；`待测` = 结论依赖运行期或 `TerrariaAmbienceAPI.dll`（不在仓库内）。

### A. 音频实例生命周期（唯一会随游玩时长单调恶化的一类）

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B1 | `Common/PositionalAudio2D.cs:53,63` | 每次 `Play()` 都 `CreateInstance()` 覆盖旧实例，旧的原生 voice 既不 `Stop` 也不 `Dispose` | 高 | 确认 |
| B2 | `Content/Players/AmbientPlayer.cs:23-25,343-357` | 静态实例 + 实例级「已初始化」标志 → 每次进世界/每个玩家都重建 2 个循环音源，旧的泄漏；过滤本地玩家的那行还被注释掉了 | 高 | 确认 |
| B3 | `Sounds/SoundFilters/FilterHooks.cs:22,25,155-159` | 两个静态 List 只 `Add` 从不删，每 tick 对每个条目做一次完完整整的线扫 | 中高 | 确认（触发条件见明细） |

### B. 每 tick 的世界扫描 / 托管分配

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B4 | `Content/Players/AmbientPlayer.cs:168-169` → `Content/CampfireDetection.cs:13-15` | 篝火检测每 tick 扫 **100×100 = 10,000 格** `Main.tile`（`campfireSounds` 默认开） | 高 | 确认 |
| B5 | `Common/Systems/AudioLoopsSystem.cs:41-74,83,88,94` | 找点的昂贵属性被写在 `if` 条件里：每 tick 至少 60 次 `Collision.SolidCollision`，夜里最坏约 180 次 | 高 | 确认 |
| B7 | `Sounds/SoundFilters/SoundFilterSystem.cs:224-249,299-335` | raycast 混响每次刷新 = 每个 tile 一条 Bresenham 线；默认开启，单次刷新最坏几十万次 tile 读 | 高 | 确认（量级） |
| B6 | `Content/AmbientAndMore/ImmersiveSoundsSystem.cs:27-28` | 每 tick 无条件 `new List<Vector2>()` 两个（末尾 `Clear()` 清的是刚要扔掉的局部变量） | 中 | 确认 |
| B8 | `Content/AmbientAndMore/AmbientHandler.cs:352` → `Helpers/GeneralHelpers.cs:487-495` | 水下时每 tick 最多 480 次 `Collision.DrownCollision`（1 像素步长） | 中 | 确认 |
| B9 | `Helpers/GeneralHelpers.cs:257-263` → `Content/AmbientAndMore/FootstepHandler.cs:138-160` | 每 tick 6 个 `new Item()` + 2 个闭包/委托，纯 Gen0 压力 | 中低 | 确认 |
| B10 | `Sounds/TileSounds/TileSoundsPlayer.cs:33-52` | 木结构声每次触发分配 1600 个 `Point` + 3200 次 `GetTileSafely`；且每个 `ModPlayer` 各跑一遍、无服务端守卫 | 中低 | 确认 |

### C. 逻辑 / 单位 / 概率写反（几行就能改对，但玩家听得出来）

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B11 | `Content/AmbientAndMore/ImmersiveSoundsSystem.cs:21-25` | `1f / Main.cloudAlpha` 无雨时得 `+∞`，雷声被彻底禁用；实际概率 ≈ `0.005 × cloudAlpha`（满雨约 3.3 秒一发） | 中 | 确认 |
| B12 | `Content/CraftSounds.cs:128-132,160-161` | `TimeSinceLastCraft = 0` 写在判断之前 → `> 60` 是死代码；`createWall >= 0` 恒真 | 中 | 确认 |
| B13 | `Content/Players/AmbientPlayer.cs:98-125` | 关箱子时 `chestClose` 与 `chestChange` 同时为真 → 播两次；猪猪/保险箱提前返回不更新 `chestStateOld` → 用过期下标；`Main.chest[idx]` 未判空 | 中 | 确认 |
| B14 | `Content/Players/AmbientPlayer.cs:244,259` | `determinedValue *= 1.3f` 乘在初值 0 上 → 金属头盔的脚步声加成永远是 0 | 中 | 确认 |
| B16 | `Common/Systems/SyncAmbienceSystem.cs:26-42` | `_didInitNextLoop = !Main.gameMenu` 写反 → 进/出世界时曲目被反复重随机（若该钩子在主菜单也跑，就是每 tick 120 次 `ChangeTrack`） | 中低 | 确认（频率待测） |
| B17 | `Content/InventorySounds/InventorySoundsGlobalItem.cs:16-20,62-63` | `item?.type <= 0` 拦不住 `null`；`_prevItem` 是静态却被多实例共用；装了 WeaponOutLite 后不再过滤非本地玩家 | 中 | 确认 |
| B19 | `Sounds/SoundFilters/SoundFilterSystem.cs:203,212,290,355-417` | `GenerateAudioFilters` 传进去的起点和终点同一个点 → 线扫长度为 0 → 全局 `LowPassIntensity` 恒为 1.0，那段低通等于没开 | 中 | 确认 |
| B18 | `Content/AddedNPCSounds/SlimeSounds.cs:82-94` | NPC 入水音用带符号 `velocity.Y` 算音量 → 上跳时可得到负音量；`vel == 0` 的兜底顺序写反 | 中低 | 确认 |
| B15 | `Content/AmbientAndMore/AmbientHandler.cs:162-165` | `Main.LocalPlayer.position.Y`（像素）和 `Main.rockLayer`（tile）比较 → 条件几乎恒真，「只在地狱里才控制」失效 | 中低 | 确认代码，影响待测 |

### D. 联机

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B20 | `TerrariaAmbience.cs:250-265` ↔ `Content/CraftSounds.cs:150-156` | 服务器转发顺序是 `Vector2 → String`，接收端却按 `String → Vector2` 读 → 别人制作的音效错位或抛异常 | 中高 | 确认 |
| B21 | `TerrariaAmbience.cs:365-366` | 未知包直接 `throw new Exception("Unidentified.")`；转发包零校验、零限速（任意客户端能让所有客户端去加载任意路径） | 中 | 确认 |
| B22 | `TerrariaAmbience.cs:282-283` | `curEveningAmb = morningAmbienceForTheDay`，明显复制粘贴 | 低 | 确认 |

### E. 服务端 / 卸载

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B23 | `Common/Systems/AudioLoopsSystem.cs:17-20`、`Sounds/SoundFilters/FilterHooks.cs:26-28`、`Common/Systems/RemoveDefaultWindAndRainSystem.cs:29-35` | 三处缺 `if (Main.dedServ) return;` → 专用服务端在 `PreSaveAndQuit` / `Unload` 必抛 NRE | 中低 | 确认 |
| B24 | `Common/Systems/AudioLoopsSystem.cs:21-38` | `OnModLoad` 的实参在构造函数之前求值 → 服务端也会 `Request<SoundEffect>` 加载 14 个 wav；构造函数里的 `if (!Main.dedServ)` 拦不住 | 中低 | 确认 |
| B25 | `Content/AmbientAndMore/NPCFootstepHandler.cs:36-43,47-49` | 用 `NPCID` 类型 ID 直接索引以 **netID** 为键的 `ContentSamples.NpcsByNetId`，且 `Dictionary.Add` 会因重复 key 抛异常 → 加载期崩溃风险 | 中 | 待测 |

### F. FAudio 后门 / 外围

| ID | 位置 | 一句话 | 严重度 | 确定性 |
|---|---|---|---|---|
| B29 | `Sounds/SoundFilters/FilterHooks.cs:141-153` | `ActiveSound_Play` 里 `orig(self)` 已经播了，之后又 `Play()` 一次 → 首个音帧未滤波 + 可能重触发 | 中低 | 确认 |
| B30 | `Sounds/SoundFilters/FAudioHacks/FAudioReverbController.cs:103-113` | 每个新音源第一次挂混响就 `FNAPlatform.Malloc` 一块原生内存且永不 `Free`，同时覆盖 FNA 共享的 `device.reverbSends` | 中 | 确认 |
| B31 | 同文件 `:56-63` | `_init` 是静态标志且不复位 → 重载 mod / 设备重建后 `ReverbVoice` 悬空；`Unload` 既不释放 ReverbVoice 也不 Free 发送描述符 | 中 | 确认 |
| B28 | `Sounds/SoundFilters/FilterHelpers.cs:45-61` | `catch` 里无条件 `Main.NewText`（漏了 `showWarnings` 判断）→ 一旦失败每 tick 往聊天框刷一行 | 中低 | 确认 |
| B26 | `Helpers/TileVisualizationHelpers.cs:25-26,44,69,103` | 调试层开着（`_visVis == 0`）时每 tick 对全房间 tile 做一次反射率判定 + 遮蔽线扫，而清空因早退永远不执行 → 字典累积 | 低 | 确认 |
| B27 | `Common/AStar.cs:54-79,112-119` | 无 closed set + 开集线性取最小（O(n²)），每扩展一个节点分配一个 `List` 和一个 `Point[4]` | 低 | 确认（当前无调用者） |

---

## 4. 明细

### B1 · `PositionalAudio2D` 每次播放都换一个新音源实例，旧的从不回收

**位置**：`Common/PositionalAudio2D.cs:53`（构造）、`:57-65`（Play）

```csharp
        _instancedAudio = Audio.CreateInstance();   // :53  构造函数里先建一个
...
        _instancedAudio = Audio.CreateInstance();   // :63  Play() 里再建一个并覆盖
        _instancedAudio?.Play();
```

**后果**：第一次 `Play()` 就把构造时那个实例丢掉了；之后每次 `Play()` 再丢一个。`_instancedAudio` 是唯一引用，全仓库对它的 `Dispose` 出现 0 次（只有 `TerrariaAmbience.cs:234-240` 对另外三个字段做 Dispose）。被丢掉但仍在播放的循环实例会继续占着原生 source voice，并继续出声——同一素材多路叠加就是音量变响/相位抵消；voice 数一路上涨，最后表现为「新音效不响了」。`AudioLoopsSystem` 那套 14 个 `PositionalAudio2D`（10 个草丛虫 + 4 个猫头鹰）在野外是「每 ~680 tick（夜里另加 ~750 tick）挑一个播一次」，也就是**每小时几百次 `Play()` = 每小时几百个泄漏的原生 voice**。

**修法（最小改动）**：

```csharp
public void Play(Vector2 position, float maxDist) {
    _maxDistance = maxDist;
    Position = position;
    curLoopTimes = Main.rand.Next(minMaxLoops.Item1, minMaxLoops.Item2 + 1) - 1;
    _instancedAudio?.Stop();
    _instancedAudio?.Dispose();
    _instancedAudio = Audio.CreateInstance();
    _instancedAudio.Play();
}
```

更省的做法是**根本不复用**：构造时建一次，`Play()` 只 `Play()` + 更新 Volume/Pan，`Update()` 里已有的循环逻辑继续用同一个实例。另外 `ToString()`（`:96`）直接读 `_instancedAudio.Volume`，为空时会 NRE——顺手加个 `?.`。

### B2 · 冰面滑行音：静态实例配实例级标志，进一次世界漏一对循环音源

**位置**：`Content/Players/AmbientPlayer.cs:23-25`（字段）、`:343-357`（初始化）、`:362-369`（使用）

```csharp
    public static SoundEffectInstance soundSlippyRoughInst;      // :23 静态
    public static SoundEffectInstance soundSlippySmoothInst;     // :24 静态
    bool _areSoundsInitialized;                                  // :25 却是实例字段
...
    public void HandleIceScraping() {
        // if (Player.whoAmI != Main.myPlayer) return;           // :344 被注释掉了
        if (!Main.dedServ) {
            if (!_areSoundsInitialized) {
                soundSlippyRoughInst = …CreateInstance();         // :348
                soundSlippyRoughInst.IsLooped = true;             // :349
                soundSlippyRoughInst?.Play();                     // :350
```

**后果**：三条独立的毛病叠在一起——

1. `ModPlayer` 每次进世界都会重建，`_areSoundsInitialized` 跟着回到 `false` → 又建 2 个 `IsLooped = true` 且立刻 `Play()` 的实例，**旧的既没 `Stop` 也没 `Dispose`，会一直在后台循环**。`TerrariaAmbience.Unload` 只 Dispose「当前这两个」，而且只在卸载 mod 时跑。
2. `PostUpdate`（`:151-155`）对**每个** player 都调 `HandleIceScraping`，本地玩家的过滤线被注释 → 多人同屏时每个玩家的 `ModPlayer` 都来初始化一次，静态字段被反复覆盖。
3. 每个玩家都拿**自己**的 `Player.slippy / velocity` 去写**同一个静态实例**的 `Volume/Pan`（`:362-369`）→ 别人在冰上滑，会改你听感。

**修法**：`_areSoundsInitialized` 改 `static`；在 `OnEnterWorld`/`OnExitWorld` 里显式 `Stop(); Dispose(); = null;` 再置标志；恢复 `if (Player.whoAmI != Main.myPlayer) return;`。

### B3 · 动态音源列表只增不减，并且每 tick 每个条目一条线扫

**位置**：`Sounds/SoundFilters/FilterHooks.cs:22,25`（两个 List）、`:155-159`（入列）、`:95-137`（每 tick 遍历）

```csharp
public static List<ActiveSound> dynamicSfxActiveSounds = [];   // :22
static List<float> _oldLowPasses = [];                        // :25
...
        if (sfx is DynamicSoundEffectInstance) {              // :156
            dynamicSfxActiveSounds.Add(self);                 // :157
            _oldLowPasses.Add(0f);                            // :158
```

**触发条件（必须说清）**：只有 `DynamicSoundEffectInstance` 才会入列，而**这个 mod 自己从不创建**这种实例（全仓库只有这两处 `is` 判断）。也就是说，纯原版 + 本 mod 的环境下列表一直是空的；一旦有别的 mod（或将来 tML/本 mod 的流式播放）创建了动态实例，就会**只进不出**。

**后果（条件成立时）**：列表里每一条（含早已停止播放的）每 tick 都要：
`CalculateBandPass` → `CalculateLowPass`（从听点向声源走一条 `TileLine`，屏幕对角线 100+ tile，就是每条约 100+ 次 `Framing.GetTileSafely`）→ `ApplyReverb`（一次原生 `FAudioVoice_SetOutputMatrix`）。换过几十次场景之后，就是「几十个已结束的音源 × 每 tick 上百次 tile 读」，没有任何上限。`PreSaveAndQuit` 也不清空。

**修法**：入列时记住 `ActiveSound` 的槽位；循环里 `!self.IsPlaying()` 或 `SoundEngine.TryGetActiveSound` 失败就 `RemoveAt`（两个 List 同步删，保证下标对齐）；再给过滤计算加个每 N tick 一次的节流（复用 `audioFiltersRefreshTime`）。

### B4 · 篝火检测每 tick 扫 10,000 格

**位置**：`Content/Players/AmbientPlayer.cs:168-169` → `Content/CampfireDetection.cs:10-27`

```csharp
        if (audioCfg.campfireSounds) {                                      // 默认 true
            var campfires = CampfireDetection.GetNearbyCampfires(Player.Center, 50);   // :169
...
    public static HashSet<Point> GetNearbyCampfires(Vector2 position, int radius = 30) {
        for (int i = pos.X - radius; i < pos.X + radius; i++) {             // 100 次
            for (int j = pos.Y - radius; j < pos.Y + radius; j++) {         // × 100 次
                if (!WorldGen.InWorld(i, j)) continue;
                var tile = Main.tile[i, j];                                 // 10,000 次/ tick
```

**后果**：`radius = 50` → 100×100 = **10,000 次 `Main.tile` 读取 + 10,000 次 `InWorld`**，每 tick 一次 = 60 万次/秒，只为找一个「最近的篝火」。篝火是静态方块，玩家的移动速度远低于此。这是本 mod 单点最贵的每 tick 循环。

**修法**：加节流（每 30 tick，或玩家位移超过 8 tile 才重扫）；或只扫「玩家脚下 ±2 tile 这一条地面线」+ 缓存命中结果；`_tileBuffer` 已经复用了，改起来很轻。

### B5 · 「找点」这个昂贵属性被写在 `if` 条件里

**位置**：`Common/Systems/AudioLoopsSystem.cs:41-74`（属性）、`:83,88,94`（调用）

```csharp
            if (RandomPosNearPlayer != Vector2.Zero)        // :83  每 tick 先扫描，再掷骰子
            {
                int chance = Main.rand.Next(0, 680);       // :85  1/680 才播
                if (chance == 0)
                    grassCritters[rand]?.Play(RandomPosNearPlayer, 0.50f);   // :88  又求值一次
                if (!Main.dayTime) {
                    int chance2 = Main.rand.Next(0, 750);
                    if (chance2 == 0)
                        owls[rand2]?.Play(RandomPosNearPlayer, 0.75f);       // :94  第三次
```

**次数（实测口径，比上一轮估计的 30 次高一个量级）**：单次求值最多 30 轮 `goto Retry`，每轮 2 次 `Collision.SolidCollision`（`:52`、`:58`）+ 1 次 `Main.tile`（`:64`）+ 2 次 `List.Contains`（`GrassBlocks` 19 项 / `DirtBlocks` 2 项）：

- 常态：每 tick **60 次 `SolidCollision`**（`:83` 一次求值）；
- 峰值（白天 1/680 或夜里 1/750 命中）：**约 180 次 `SolidCollision`/tick**；
- 另加 30~90 次 `Main.tile` 与 60~180 次 `List.Contains`。

**额外副作用**：`:88`/`:94` 的第二次求值会**重新随机**一个位置（和判定时用的不是同一个点），并且在 30 轮内没找到合法点时会返回 `Vector2.Zero` —— 那个 `Play(Vector2.Zero, …)` 会把声音放到世界原点 (0,0)。

**修法**：先掷骰子，命中后再取点，并把结果存进局部变量复用：

```csharp
int chance = Main.rand.Next(0, 680);
if (chance != 0) return;                       // 还没到播的时候，一次都不扫
var pos = RandomPosNearPlayer;
if (pos == Vector2.Zero) return;
grassCritters[Main.rand.Next(grassCritters.Length)]?.Play(pos, 0.50f);
```

顺带补 `if (Main.gameMenu || !Main.LocalPlayer.active) return;` —— 同目录的 `FootstepHandler.Update` 有 `if (Main.gameMenu) return;`（`:139`）、`AmbientHandler.Update` 有 `if (Main.dedServ) return;`（`:269`），只有这里两个守卫都没有。

### B7 · raycast 混响：每个 tile 一条 Bresenham 线，默认还是开着的

**位置**：`Sounds/SoundFilters/SoundFilterSystem.cs:230-249`（并行分支）、`:299-319`（`ProcessTile`）、`:322-335`（`IsPathBlocked`）

```csharp
        if (doRaycast) {
            // expensive so use sparingly
            if (IsPathBlocked(listenerCoords, tilePos)) return;   // :306 每个 High 反射的 tile 走一次
        }
...
    public static bool IsPathBlocked(Point lpc, Point tilePos) {
        TileLine(lpc, tilePos, (x, y, t) => { … });               // 线长 ≈ 屏幕对角线 tile 数
```

**后果**：`room.Tiles` 上限由 `MaxRoomArea` 决定（默认 2500，`FloodFillSystem`），地下房间里绝大多数 tile 反射率是 `High` → 几乎每个 tile 都要走一条 100+ tile 的线。单次刷新最坏 **约 30 万次 `Framing.GetTileSafely`**，每 `audioFiltersRefreshTime`（默认 10）tick 触发一次。`reverbUsingRaycasting` 默认 `true`。

**修法**：加粗筛——只对「距离 < 40 tile 且反射率 High」的 tile 做遮蔽；或者按监听 tile 缓存结果，玩家跨 tile 才重算。不要动 attach 流程（见 §8「不要碰」）。

### B6 · 每 tick 两个 `List<Vector2>`

**位置**：`Content/AmbientAndMore/ImmersiveSoundsSystem.cs:27-28`，末尾 `:104-105`

```csharp
        var eligibleThunderLocations = new List<Vector2>();     // :27
        var eligibleHowlLocations = new List<Vector2>();        // :28
...
        eligibleHowlLocations.Clear();                          // :104  清的是马上要被扔掉的局部变量
        eligibleThunderLocations.Clear();
```

**后果**：不管下不下雨、有没有雷，每 tick 2 次分配（≈120 次/秒）。`Clear()` 纯属无效动作。

**修法**：把两个 List 提成 `static readonly` 字段，用前 `Clear()`、用完不用管（或改成先算数量再随机选一个玩家，连 List 都不需要）。

### B8 · 水下深度：1 像素步长，最多 480 次 `DrownCollision`

**位置**：`Content/AmbientAndMore/AmbientHandler.cs:352-362` → `Helpers/GeneralHelpers.cs:487-495`

```csharp
            var tilesToCheck = 30;
            var waterDepth = player.GetModPlayer<AmbientPlayer>().GetWaterPressureFloat(tilesToCheck * tileSize); // 480
...
        for (int i = (int)checkPos.Y; i > (int)checkPos.Y - heightCheck; i--) {
            bool isWaterExistent = Collision.DrownCollision(checkPos - new Vector2(0, iterations), 1, 1, -1);
```

**后果**：只要玩家在水里，每 tick 最多 480 次 `DrownCollision`（每次内部还要查若干 tile）——而需求只是「到水面还有多少像素」。

**修法**：先按 16 像素步长找到「没水」的边界，再在最后 1 tile 内逐像素细化；或结果按 10 tick 缓存。

### B9 / B10 · 每 tick 的 `new Item()`、以及木结构声的 1600 个 Point

**位置**：`Helpers/GeneralHelpers.cs:257-263`、`Content/AmbientAndMore/FootstepHandler.cs:138-160`、`Sounds/TileSounds/TileSoundsPlayer.cs:33-52`

```csharp
    public static bool CheckPlayerArmorSlot(Player player, int slot, out Item item) {
        item = new Item();                                  // :258  无条件 new，哪怕槽位是空的
        if (!player.armor[slot].IsAir) item = player.armor[slot];
```

```csharp
        _woodsBuffer.Clear();
        var tileList = GeneralHelpers.GetTileSquareCoordinates(playerTilePos.X, playerTilePos.Y, 40, 40);  // 1600 个 Point
```

**后果**：脚步每 tick 6 次 `new Item()` + 2 个闭包（≈480 次分配/秒）；木结构声每次触发 1600 个 `Point` + 3200 次 `GetTileSafely`，而且是**每个 `ModPlayer` 都跑一遍、没有 `dedServ` 守卫**，多人同屏线性放大。

**修法**：`CheckPlayerArmorSlot` 直接 `item = player.armor[slot]; return !item.IsAir;`；`ForEach` 换 `for`；`TileSoundsPlayer` 加 `whoAmI != Main.myPlayer` 与 `dedServ` 早退，扫描改步长抽样。

### B11 · 雷声概率：`1f / cloudAlpha`

**位置**：`Content/AmbientAndMore/ImmersiveSoundsSystem.cs:21-25`

```csharp
        float rainingChanceNumber = 1f / Main.cloudAlpha;
        float thunderChance = Main.rand.NextFloat(rainingChanceNumber);
```

**事实（比上一轮的说法收窄）**：`1f/0f` 在 IEEE754 下是 `+∞`，不是 NaN；`NextFloat(∞)` 得到 `+∞`（只有 `NextDouble()` 恰好返回 0 时才会 `0×∞ = NaN`，概率 2⁻⁵³）。`+∞ < 0.005f` 与 `NaN < 0.005f` 都是 `false` —— 所以**不会**有 NaN 流进音量，净效果只是「没雨时没有雷」。真正的毛病是标定：`P(每 tick 出雷) ≈ 0.005 × cloudAlpha`，满雨（`cloudAlpha = 1`）时平均 **3.3 秒一发**，而且是从随机玩家的位置发。

**修法**：语义直接写清楚，别用倒数：

```csharp
// 别写成 1f / cloudAlpha：cloudAlpha 归零时那是个无意义的 ∞
bool thunder = Main.cloudAlpha > 0.02f
            && Main.rand.NextFloat() < 0.005f * Main.cloudAlpha;
```

### B12 · 制作音效的节流判断被前置赋值废掉

**位置**：`Content/CraftSounds.cs:128-132`、`:160-161`

```csharp
        TimeSinceLastCraft = 0;                                        // :128  先清零

        if (LastCraftedConsumable != item.type || TimeSinceLastCraft > 60) {   // :132  > 60 永远为假
...
        if (item.createTile >= TileID.Dirt || item.createWall >= 0 || item.consumable)   // :160
            LastCraftedConsumable = item.type;
```

**后果**：`TimeSinceLastCraft > 60` 是死代码 → 连续制作同一种物品时，第二件起没有声音（只能靠换成别的物品解锁）。`createTile/createWall` 用 0 表示「无」，`>= 0` / `>= TileID.Dirt(0)` 对所有物品恒真 → `LastCraftedConsumable` 无条件被覆盖，`consumable` 条件形同虚设。

**修法**：`TimeSinceLastCraft = 0;` 挪到判断之后；`:160` 改成 `> 0`。作者自己的注释（"why tf is TimeSinceLastCraft shitting its pants? we will never know"）说明他当时也没查出来——原因就在这里。

### B13 · 箱子：关一次播两遍，猪猪/保险箱之后下标过期

**位置**：`Content/Players/AmbientPlayer.cs:98-127`

```csharp
        if (chestStateNew < -1) return;                                       // :98  -2/-3 直接走，不更新 chestStateOld
        var chestClose  = (chestStateNew != chestStateOld) && chestStateNew == -1;   // :101
        var chestChange = (chestStateNew != chestStateOld) && chestStateOld > -1;    // :102
        …
        else if (chestClose) { … PlaySound(chest_close …) }                    // :113-118
        if (chestChange)     { … PlaySound(chest_close …) }                    // :120-125
```

**后果**：纯关闭（`new = -1`，`old > -1`）时两个条件**同时为真** → 同一 tick 播两次 `chest_close`，两次 `NaturalPitchVarianceLesser` 不同 → 明显的双响/拍频。开过猪猪存钱罐（`-2`）或保险箱（`-3`）后 `chestStateOld` 停在旧箱子号，关箱子时会在**错误的箱子位置**播声音；`Main.chest[idx]` 允许为 `null`，`:107/:114/:121` 都直接解引用。

**修法**：`chestChange` 改成 `… && chestStateNew > -1`（即「换箱子」而不是「包括关闭」）；`:98` 先 `chestStateOld = chestStateNew; return;`；取 `Main.chest` 前加 `idx >= 0 && idx < Main.chest.Length && Main.chest[idx] != null`。

### B14 · 金属头盔的脚步声加成乘在 0 上

**位置**：`Content/Players/AmbientPlayer.cs:244,255-262`（`GetVanityStepVolume:294-298` 同样）

```csharp
        float determinedValue = 0f;                       // :244
…
                        determinedValue *= 1.25f + 0.05f; // :259  0 × 1.3 = 0
```

**后果**：头盔分支永远贡献 0，最终音量只由胸甲 `+= 0.05f`、腿甲 `+= 0.01f` 决定（最大 0.06）。

**修法**：两处 `*=` 改 `+=`（`GetArmorStepVolume` 的头盔 `+= 0.05f`，`GetVanityStepVolume` 的头盔 `+= 0.1f`；具体数值由作者定，关键是别把乘数乘在初值 0 上）。

### B15 · Hell 轨道的激活条件混用了像素和 tile

**位置**：`Content/AmbientAndMore/AmbientHandler.cs:162-165`

```csharp
        Hell = new(mod, AmbientPath + "biome/hell/rumbles", "Hell", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            // save some resources by only controlling this if they're in the underworld
            return Main.LocalPlayer.position.Y > Main.rockLayer;
        });
```

**后果**：`Main.rockLayer` 是 **tile** 坐标（同仓库 `SoundFilterSystem.cs:485` 就是拿 tile 坐标跟它比），`position.Y` 是**像素**（像素换算是 `Main.worldSurface * 16`，见 `GradientGlobals.cs:80`）。`position.Y > 300` 在世界里几乎恒真 → 注释里说的「只在地狱里才控制这条轨道以省资源」完全没生效。**影响待测**：真正后果取决于 `TerrariaAmbienceAPI` 的 `ModAmbience` 怎么用这个委托（不在仓库内），最轻是每 tick 无意义的状态更新，最重是地狱循环在全世界一直被保持激活。

**修法**：`return Main.LocalPlayer.Center.Y > (Main.maxTilesY - 300) * 16;`，或直接复用已有的梯度 `GradientGlobals` 判定。

### B16 · 初始化标志写反，曲目会被反复重随机

**位置**：`Common/Systems/SyncAmbienceSystem.cs:26-42`

```csharp
        if (!_didInitNextLoop) {
            if (!Main.dedServ) {
                if (Main.dayTime) TerrariaAmbience.DefaultAmbientHandler?.RandomizeDuskTracks();
                else              TerrariaAmbience.DefaultAmbientHandler?.RandomizeDawnTracks();
            }
            …
        }
        _didInitNextLoop = !Main.gameMenu;      // :42  只要钩子在菜单里跑，这里就恒为 false
```

**后果**：`_didInitNextLoop` 被赋成 `!Main.gameMenu`，所以**只要这个钩子在主菜单里也被调用**，它就是每 tick 被重置为 `false`、下 tick 又进 `if` → 每 tick 重新随机一次、每次两个 `ChangeTrack`（120 次/秒）。即使钩子不在主菜单跑，「离开世界 → 回菜单 → 再进世界」这条路径也会让曲目在进世界前后被反复重随机。

**两处待测**（都只要一行日志）：① `ModSystem.PostUpdateEverything` 在 `Main.gameMenu == true` 时是否真的被调用；② 专用服务端上 `Main.gameMenu` 的取值——因为 `:35-39` 那个分支会 `SendPM/SendAM`，如果服务端也恒为 `true`，就是每 tick 两包。

**修法**：改成只在「进入世界」这一瞬间置 `true`（`OnEnterWorld`，或用 `!Main.gameMenu && !_didInitNextLoop` 置位），退出世界时复位。这样两种钩子行为下都正确。

### B17 · 物品栏音效：null 拦不住、静态 `_prevItem`、WeaponOut 分支

**位置**：`Content/InventorySounds/InventorySoundsGlobalItem.cs:19-20,16,62-63`

```csharp
        if (!TerrariaAmbience.WeaponOutLoaded)
            if (Player.whoAmI != Main.myPlayer) return;      // :19-20  装了 WeaponOutLite 就不早退了
…
    static Item _prevItem;                                   // :16  被所有 ModPlayer 实例共用
…
        if (item?.type <= 0) return;                         // :63  item 为 null 时 int? 比较结果是 false
```

**后果**：① 装了 WeaponOutLite（`WeaponOutLoaded == true`）时不再过滤非本地玩家，而 `Main.mouseItem` 是客户端全局的 → 光标物品一变，世界里 N 个玩家的 `ModPlayer` 各播一次 swap 音（音量叠加、位置错）。② `_prevItem` 静态导致 `PuttingDown` 比较的是别人的历史物品。③ `item?.type <= 0` 对 `null` 为 `false`，guard 没拦住，接着 `ItemClassifier.GetSoundType(item)` 里解引用就 NRE（`ItemClassifier.cs:27` 是同样的写法，一起改）。

**修法**：`if (item is null || item.type <= 0) return;`（两处）；`_prevItem` 改实例字段；恢复非本地玩家早退。

### B19 · 全局低通强度恒为 1.0（功能静默失效）

**位置**：`Sounds/SoundFilters/SoundFilterSystem.cs:203,212,290`（传参）、`:355-358`、`:364-417`（计算）

```csharp
        SetFilterValues(pos, Vector2.Zero, ref fParam, playerUnderwater);   // pos == ScreenListeningPosition
...
    public static float CalculateLowPass(Vector2 position, Vector2 offset, out bool enabled) {
        var goalPos = ScreenListeningPosition;
        TileLine(goalPos.ToTileCoordinates(), (position + offset).ToTileCoordinates(), …);   // 起点 == 终点
```

**后果**：`TileLine` 在起点即终点时立刻 `break`（`:529-530`）→ `numBlockingTiles = 0` → `occlusion = 1`、`curve = 1` → `LatestParams.LowPassIntensity` 永远是 1.0，`ApplyFiltersQuick` 里那条低通等于全开。**注意**：`ActiveSoundFilter`/`DynamicSfx` 两条路径传的是真实声源位置，那部分是有效的；失效的只有「全局参数」这一份。`enabled = true;`（`:412`）也是常量，`out bool enabled` 没有实际作用。

**修法**：要么让 `GenerateAudioFilters` 传真正的听点（`Main.LocalPlayer.Center`），要么删掉这份全局低通，只保留按声源计算的那条。

### B20 · 联机：制作音效的包顺序发/收不一致

**位置**：发送 `Content/CraftSounds.cs:150-156`；服务器转发 `TerrariaAmbience.cs:251-257`；客户端接收 `TerrariaAmbience.cs:258-263`

```csharp
// 发送（客户端）
        p.Write(TAPID.SEND_CRAFT);
        p.WriteVector2(player.Center);   // 先 Vector2
        p.Write(dir);                    // 后 String

// 服务器转发
        p.Write(packet);
        p.Write(reader.ReadVector2());   // 顺序保持
        p.Write(reader.ReadString());

// 接收（其它客户端）
        var path = reader.ReadString();  // ← 先读 String
        var pos  = reader.ReadVector2(); // ← 再读 Vector2
```

**后果**：接收端拿 `Vector2` 的第一个字节当 7-bit 字符串长度解析 → 要么直接 `EndOfStreamException`/`ArgumentOutOfRangeException`，要么读出一个垃圾路径再让 `SoundStyle` 加载失败。也就是说**多人游戏里别人的制作音效基本是坏的**（单人根本走不到这条路径，所以不容易被发现）。

**修法**：接收端改成先 `ReadVector2()` 再 `ReadString()`（跟发送/转发保持一致）——这是三处里最小的改动。顺带把「顺序」写成注释，避免下次又改错一边。

### B21 · 未知包直接抛异常；转发无校验无限速

**位置**：`TerrariaAmbience.cs:365-366`，以及 `:246-323` 的四个 `if (Main.dedServ)` 转发分支

```csharp
            default:
                throw new Exception("Unidentified.");
```

**后果**：① 任何一个未知/被截断的包都会在 `HandlePacket` 里抛异常（服务端上可能触发断连/被反作弊盯上，客户端上是日志刷屏）。② `SEND_CRAFT`/`SEND_AMB_SFX` 的转发不校验路径、不限速 → 恶意或出错客户端可以让**所有**客户端去加载任意资源路径（`SoundStyle` 加载失败会抛），也能刷爆带宽。

**修法**：`default` 改成 `Logger.Warn` + `break`；转发前校验 `path` 前缀（必须 `TerrariaAmbience/` 开头）与长度，并对 `whoAmI` 加简单限速。

### B23 · 三处缺服务端守卫，服务器「保存退出 / 卸载」必报 NRE

| 位置 | 为什么在服务端必炸 |
|---|---|
| `Common/Systems/AudioLoopsSystem.cs:17-20` | `PreSaveAndQuit` 直接调 `DefaultAmbientHandler.RandomizeDawnTracks()`；而 `AmbientHandler.Initialize()` 第一行就是 `if (Main.dedServ) return;`（`AmbientHandler.cs:90`）→ `ForestDay/ForestMorning` 全是 `null`，`ChangeTrack` 处 NRE（`:265`） |
| `Sounds/SoundFilters/FilterHooks.cs:26-28` | `CampfireCrackleInstance.Stop()`；该字段在 `TerrariaAmbience.cs:211-221` 里因为 `if (Main.dedServ) return;` 永远是 `null` |
| `Common/Systems/RemoveDefaultWindAndRainSystem.cs:29-35` | `Unload` 没有 `Load` 里那句 `if (Main.dedServ) return;` → `Main.audioSystem as LegacyAudioSystem` 为 `null`，再取 `.AudioTracks[28]` |

**后果**：玩家客户端看不到，但服务器关机/`Reload Mods` 时日志里必有一串 `NullReferenceException`；`Reload Mods` 场景下可能连带影响后续卸载流程。

**修法**：三处统一加 `if (Main.dedServ) return;`（顺带对 `DefaultAmbientHandler?.ForestDay` 判空）。

### B24 · 服务端也在加载 14 个音频资源

**位置**：`Common/Systems/AudioLoopsSystem.cs:21-38`，守卫在 `Common/PositionalAudio2D.cs:45-55` 的构造函数里

```csharp
        grassCritters[0] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "…/crickets1"), …);
```

**后果**：C# 先求值实参再进构造函数，所以 `GetAssetValue<SoundEffect>`（内部 `Assets.Request<SoundEffect>(…, ImmediateLoad).Value`）在专用服务端照样执行，14 个 wav 会被解码进内存；构造函数里的 `if (!Main.dedServ)` 只挡住了 `CreateInstance()`。

**修法**：`OnModLoad` 开头 `if (Main.dedServ) return;`；或把「路径」而不是 `SoundEffect` 传进构造函数。

### B25 · NPC 脚步表：类型 ID 当 netID 用，`Dictionary.Add` 还可能撞 key（待测）

**位置**：`Content/AmbientAndMore/NPCFootstepHandler.cs:36-43`、`:47-49`

```csharp
        for (int i = 0; i < NPCID.Search.Count; i++) {
            var name = NPCID.Search.GetName(i);
            if (name.Contains("zombie", …)) {
                if (ContentSamples.NpcsByNetId[i].frame.Height < 150) {   // i 是 type，字典键是 netID
                    zombieIds.Add(i);
                }
            }
        }
```

**后果**：`ContentSamples.NpcsByNetId` 以 **netID** 为键（同函数 `:20-22` 自己就是用 `elem.Value.type` 当值用的），这里却拿 `NPCID.Search` 的 **type** 直接下标，没有 `TryGetValue` → 只要有一个 type 没有同号 netID 条目，`PostAddRecipes` 阶段就抛 `KeyNotFoundException`，表现为「mod 加载失败」。另外 `AddGeneric/AddZombie` 用 `Dictionary.Add`，同一 type 出现两次就抛 `ArgumentException`。

**为什么标「待测」**：vanilla 里绝大多数 NPC 的 type == netID，我无法在不进游戏的情况下确认是否真的存在例外、以及那段循环实际覆盖到哪些 type。**验证方式**：进一次世界看日志有没有 `KeyNotFoundException`；或用 `dotnet-script` 反射扫 `NPCID.Search.Count` 与 `NpcsByNetId` 的键集合差集。

**修法**：`TryGetValue` + 索引赋值替代 `Add`。

### B29 · 带位置的音效会被「先播一遍、再滤一遍、再播一遍」

**位置**：`Sounds/SoundFilters/FilterHooks.cs:141-153`

```csharp
	void ActiveSound_Play(On_ActiveSound.orig_Play orig, ActiveSound self) {
		orig(self);                          // ← 到这里声音已经开始播了
        var vol = self.Sound.Volume;
		self.Sound.Volume = 0;
        ActiveSoundFilter(self, self.Sound); // ← 滤镜是这时候才挂上去的
        self.Sound.Volume = vol;
        self.Sound.Play();                   // ← 再 Play 一次
    }
```

**后果**：本意是「先挂滤镜再播」，但 `orig(self)` 已经把播放启动了，于是①第一个音帧是**没滤波**的原声；②对同一个实例再调一次 `Play()`，轻则无操作，重则按 FNA 实现重新触发（短音效听得出「咔」一下重起）。用 IL 钩子注释掉 `orig` 里的 `Play` 才是作者原本想做的事（同文件 `:67-87` 的 `IL_ActiveSound_Play` 就是为此写的，但它在 `:32` 被注释掉了，所以那段死代码还留着）。

**修法**：要么启用 `IL_ActiveSound_Play`（在原生 `Play` 之前插过滤），要么保持 `On_` 钩子但**不**再调 `self.Sound.Play()`——因为 `orig` 已经播了，只补 Volume/滤镜即可。

### B30 / B31 · FAudio 后门的两个确定性泄漏

**位置**：`Sounds/SoundFilters/FAudioHacks/FAudioReverbController.cs:103-113`、`:56-63`

```csharp
        device.reverbSends = new FAudio.FAudioVoiceSends();
        device.reverbSends.SendCount = 2;
        device.reverbSends.pSends = FNAPlatform.Malloc(2 * MarshalHelper.SizeOf<FAudio.FAudioSendDescriptor>());  // 每次 attach 都 Malloc，从不 Free
```

```csharp
    static bool _init;
    public unsafe static void RvInit() {
        if (_init) return;
        _init = true;                       // 静态标志；mod 重载 / 设备重建后不复位
```

**后果**：① 每个新音源实例第一次被过滤（`!inst.usingReverb`）就 `Malloc` 一块非托管内存并覆盖 `device.reverbSends`，旧块无人 `Free` → 稳定的原生堆增长；② `device.reverbSends` 是 FNA 的共享字段，被永久改成「master + reverb 两路输出」，FNA 自己再引用它创建 voice 时会带上本 mod 的 send 列表；③ `_init` 静态不复位 → `Reload Mods` 后 `device.ReverbVoice` 指向已销毁的 voice；`Unload` 既不释放 `ReverbVoice` 也不 Free 描述符。

**修法**：`pSends` 缓冲按 device 缓存一份、只 Malloc 一次；`_init` 改成「检查 `device.ReverbVoice` 是否有效」而不是一次性布尔；`Unload` 里 `FAudioVoice_DestroyVoice` + `Free`。**不要重写 attach 流程**（见 §8）。

### 其余（低）

- **B18** `Content/AddedNPCSounds/SlimeSounds.cs:82-94`：玩家版用 `Math.Abs(velocity.Y)`，NPC 版用带符号值 → 上跳离开水面时 `Volume` 为负；`vel == 0` 的兜底判断放在 `vel < 10f` 之后。修法：`Math.Abs` + 调换判序。
- **B22** `TerrariaAmbience.cs:282-283`：`SyncAmbienceSystem.curEveningAmb = DefaultAmbientHandler.morningAmbienceForTheDay;` —— 傍晚拿到早晨的值，复制粘贴。
- **B26** `Helpers/TileVisualizationHelpers.cs:25-26,44,69,103`：调试可视化（`debugInterface`）打开而 `_visVis == 0` 时，`:69` 的条件**不会**早退 → 每 tick 对 `PlayerRoom.Tiles`（上限 ≈2500）逐个做一次 `CalculateAcousticReflectivity`，开了 raycast 还要每 tile 走一条 `IsPathBlocked` 线扫——比正常的混响刷新（每 10 tick）**频繁 10 倍**；而清空只发生在 `PostDrawTiles`（`:44`），它前面 `if (_visVis == 0) return;`（`:26`）先返回 → 字典随走过的房间不断累积，直到按 RightAlt 切层或打开全屏地图。另外 `:103` 的 `(wl != Cavern || wl != Dirt)` 恒真，过滤意图失效。
- **B27** `Common/AStar.cs:54-79,112-119`：开集用 `HashSet` 线性取最小、无 closed set、`GetNeighbors` 每节点 `new List` + `new Point[4]`。当前**唯一调用点在 `SoundFilterSystem.cs:370-388` 的注释里**，没有运行时开销——但它是随时会被重新启用的「定时炸弹」，启用点正好是最热的滤镜刷新路径。
- **B28** `Sounds/SoundFilters/FilterHelpers.cs:45-61`：`ApplyLowPassFilter` 的 `catch` 无条件 `Main.NewText`（其它几个方法都有 `showWarnings` 判断）→ 一旦失败就每 tick 往聊天框刷一行。

---

## 5. 上一轮 7 条结论的复核（含 2 处更正）

| 上轮# | 结论 | HEAD 现状 | 现在对应 |
|---|---|---|---|
| 1 | `PositionalAudio2D.Play()` 每次 `CreateInstance` 覆盖旧实例、从不 `Dispose`；`IsPlaying` 未判空会 NRE | **前半仍在；后半不成立** —— `IsPlaying` 是扩展方法，内部先 `sfx == null` 判定（`Helpers/GeneralHelpers.cs:393-400`），返回 `false` 而不是抛异常。真正没判空的只剩 `ToString()`（`:96`） | B1（`IsPlaying` 那条删掉） |
| 2 | `1f / Main.cloudAlpha` → `Infinity` → `NaN` | **前半仍在，后半不成立** —— `1f/0f = +∞`（不是 NaN）；`NextFloat(∞)` 也是 `+∞`，`< 0.005f` 恒假，NaN 不会流进音量。净效果是「无雨时没有雷」，不是崩溃 | B11（改写成概率标定问题） |
| 3 | `AudioLoopsSystem` 每 tick 有 `List` 分配 | **不成立** —— 该文件里没有 `List`/LINQ，全是结构体运算。真正每 tick 两个 `List<Vector2>` 的在 `ImmersiveSoundsSystem` | B5（找点）+ B6（List） |
| 4 | `RandomPosNearPlayer` 每 tick 最多 30 次 `SolidCollision`，且写在 `if` 条件里 | **仍在，但 30 次严重低估**：每次求值 30 轮 × 2 次 = 60 次，夜里属性最多求值 3 次 → 峰值约 180 次/tick | B5 |
| 5 | 音量手算（above ground 之类），不走 FNA 距离衰减 | **仍在**，而且有两套互相不一致的手算：`PositionalAudio2D` 用 `screenWidth × distance`、屏幕中心做原点；`GeneralHelpers` 用 `screenWidth × 1.5`、屏幕左上角做距离原点。全仓库 `Apply3D` 零调用 | —（设计选择，见下方「关于音量」） |
| 6 | `AmbientHandler`/`FootstepHandler` 里每 tick 的昂贵扫描或分配 | **部分成立**：这两个类本体没有每 tick 的世界扫描，但有每 tick 分配（6 个 `new Item()` + 闭包）与 480 次 `DrownCollision`；真正 10,000 格的那次扫描挂在它们调用的 `CampfireDetection` 上 | B4 / B8 / B9 |
| 7 | `SoundFilterSystem` 的 FAudio 滤镜/混响 | **文件已被作者重构**（HashSet 白名单、`ReverbCounts` 结构体、`Parallel.ForEach`），旧结论不能直接套；重构后引入了新的量级问题与一处静默失效 | B7 / B19 / B30 / B31 |

**关于音量（上轮第 5 条的补充）**：这是**设计选择**不是 bug —— 全仓库没有一次 `Apply3D`，响度完全由「像素距离 ÷ 屏幕宽度 × 常数」决定，所以同一个世界距离在 1280 宽和 3440 宽的屏幕上衰减速度差约 2.7 倍。要改就得统一成「tile 距离 ÷ 固定 tile 半径」，这属于手感改动，建议单独一条 PR，别混进 bug 修复里。

---

## 6. 已排除：这次「下雨卡死」不是这个 mod 引起的

同一个会话里先做的取证（原始记录在 `E:\tmod recipe\docs\diagnostics\RAIN-FREEZE-2026-11-18.md`），结论摘要：

- 日志停在 **21:31:50 CalamityOverhaul 的 `KiameGateSpawn`**，无托管异常；21:32:10 是非正常关机（不是崩溃栈）。
- tML 自报进程私有提交 **10.4 → 12.1 GB**、工作集 4.5 GB、全进程提交 20.2 GB > 物理 15.8 GB；而当时**分页文件只有 C: 16 GB**（E: 虽配了 32–64 GB 但文件不存在），提交上限 31.81 GB → 这是内存/提交耗尽，不是逻辑死循环。
- 扫了 402 个 `.tmod` / 554 个 DLL 的 `cloudAlpha` 写入点：**没有任何模组写 > 1**（Coralite 用 `Clamp`、NoxusBoss 最高 0.4、ImproveGame 0.7）。原版 `Main.UpdateWeather` 里 `while (numClouds < 200 * cloudAlpha)` 在 `cloudAlpha > 1` 时会死循环，但没人把它推过 1。
- 本仓库里 `cloudAlpha` 只出现 6 次，5 次是 `GradientGlobals` 的**只读**梯度，1 次就是 B11 那个除法。

**结论**：TerrariaAmbience 与那次卡死无因果。装它会让内存基线更高（音频解码 ≈997 MB），但触发点不是它。

---

## 7. 只能实机验证的清单

| # | 问题 | 怎么验 | 预期 |
|---|---|---|---|
| 1 | B20 联机制作音效包顺序 | 两个客户端进同一服，一人合成，看另一人 `client.log` 有没有 `EndOfStreamException` / `SoundStyle` 加载失败 | 现在大概率报错；改完顺序后消失 |
| 2 | B25 NPC 脚步表加载期崩溃 | 进一次世界，看日志有没有 `KeyNotFoundException`；或离线反射比对 `NPCID.Search` 与 `NpcsByNetId` 键集 | 若存在 type≠netID 的 NPC 就会炸 |
| 3 | B16 `PostUpdateEverything` 在菜单/服务端是否也跑 | 在 `SyncAmbienceSystem.PostUpdateEverything` 开头打一行 `Main.gameMenu` / `Main.dedServ` 日志，分别进主菜单与服务端跑图 | 若菜单里也跑 → 每 tick 重随机；若服务端 `gameMenu == true` → 每 tick 两包 |
| 4 | B7 混响刷新的真实开销 | 用 PerfProbe（`E:\tmod recipe\ModSources\PerfProbe`）的 `stalls.txt` 对比 `reverbUsingRaycasting` 开/关 | 关掉后地下场景帧时间应明显下降 |
| 5 | B15 Hell 条件恒真后的影响 | 关掉 `TerrariaAmbienceAPI` 的日志开关，观察非地狱场景 `Hell` 轨道是否一直被控制 | 取决于 API 实现（不在仓库内） |
| 6 | B19 全局低通恒 1.0 | 在水下/隔墙前后抓 `LatestParams.LowPassIntensity` | 恒为 1.0 即确认失效 |

---

## 8. 建议的开工顺序

按「改动小、收益大、不碰炸药」排序，每条都能独立成 PR：

1. **PR-1 资源生命周期**：B1、B2、B3。三处都是「用完不还」，改法局部、行为兼容，是最有说服力的一组（尤其 B1，能对上社区长期抱怨的「越玩声音越怪」）。
2. **PR-2 每 tick 开销**：B4（篝火 10,000 格）、B5（找点 60 次）、B9（`new Item()`）、B8（480 次 `DrownCollision`）。纯性能，不改听感，带 before/after 帧时间数据最好。
3. **PR-3 逻辑写反**：B11、B12、B13、B14、B18。几行一个，玩家能直接听出差别。
4. **PR-4 联机与服务端**：B20（包顺序）、B21（未知包）、B23（三处 `dedServ` 守卫）。B20 是**真 bug** 且修复极小，优先级可以再往前挪。
5. **PR-5 FAudio 周边**：B30、B31。只修「别重复 Malloc / 别留悬空 voice」，不重构 attach。

**不要碰的**（动了容易连带静音/崩溃，收益也不对等）：

- `FAudioReverbController.ReverbAttach` 整套流程 —— 它直接摸 `SoundEffectInstance.handle`、`device.ReverbVoice`，依赖 `Krafs.Publicizer` 暴露的 FNA 内部字段。主菜单里那句「音频设备请设到 48000Hz 以下」的警告说明作者已经在这里踩过坑。
- `RemoveDefaultWindAndRainSystem` 把 `AudioTracks[28]/[45] = null` + 两个 `On_LegacyAudioSystem` 早退 hook —— 这是从原版音频系统里挖洞，除非能实地回归原版风雨音效，否则只加 `dedServ` 守卫就收手。
- 音量/衰减的手算体系（§5 末尾）—— 是设计选择，改动会牵动所有环境音的听感，应该单独讨论。

---

## 9. 复现与验证手法

- **计数类**（B4/B5/B8/B9）：临时在循环里加静态计数器，每 300 tick `Logger.Debug` 打印一次，改完对比同一场景同一时长。
- **泄漏类**（B1/B2/B3/B30）：长跑 + 任务管理器看进程私有提交曲线；或加一个「存活 `SoundEffectInstance` 计数」的调试命令（`Dispose` 前后各打一次）。
- **联机类**（B20/B21）：1 主机 + 2 客户端，其中一人合成/触发，另一人看 `client.log`；服务端另存一份 `server.log` 对照。
- **卸载类**（B23/B24/B31）：服务器上 `Reload Mods`，或 `Exit` 后看 `server.log`；客户端用「返回主菜单 → 再进世界」验 B2 的泄漏。
- **性能类**（B7）：PerfProbe 的 `stalls.txt`（`E:\tmod recipe\ModSources\PerfProbe`）+ `tools\perf-probe-analyze.ps1`；对照开关 `reverbUsingRaycasting`、`campfireSounds` 各跑一段同一场景。

---

## 10. 变更记录

| 日期 | 变更 |
|---|---|
| 2026-11-18 | 首版：clone fork（`77291bd`）→ 反编译发布包对照 → 通读 HEAD → 复核上一轮 7 条（更正 2 条）→ 建立 B1–B31 台账 |
| 2026-11-21 | 新增 §11：`stream-1.4` 分支的流式改造进度（准备阶段，零代码改动） |

> 本次只新增了本文件，未提交、未推送；仓库其余文件保持 clone 时的状态。

---

## 11. 流式改造进度（`stream-1.4`，2026-11-21）

> **本节与上面 §1–§9 的关系**：§1–§9 是 `1.4` 基线（`77291bd`）的缺陷台账，保持原样不改。
> 从本节起，本文件同时记录 `stream-1.4` 分支的流式改造进展。两条线**不回灌、不合并**。

### 11.1 分支与工作副本

| 项 | 值 |
|---|---|
| 分支 | `stream-1.4`，从 `1.4` 的 `66d0e73` 切出（2026-11-21） |
| 工作副本 | `D:\Terraria-Ambience-Stream-branch`（专绑 `stream-1.4`）；`E:\terraria-ambience-fork` 留给 `1.4` |
| 远端 | `origin` = `github.com/ora233-fantaea/TerrariaAmbience-fantasea` |
| 本分支提交 | `9e3f8ba` 可行性验证 → `b30d7bf` 风险预案 |
| 与 `1.4` 的关系 | 独立演进；`1.4` 上的 `9a335de`、`27bd268`（采样率警告、README）**不在**本分支 |

### 11.2 当前状态

- 阶段：**准备完成，未写任何实现代码**。
- 代码改动：0 行（仅 `docs/`）。
- 已产出：`docs/streaming-verification-2026-11-21.md`（可行性验证）、`docs/streaming-contingency-plan-2026-11-21.md`（风险预案）。

### 11.3 实测基线（本机，非估算）

| 项 | 结果 |
|---|---|
| biome 环境床 | 43 个文件 / 解码 **369.2 MB**（与旧方案 369 MB 吻合） |
| environment + rain | 5 + 3 = 8 个 / **23.6 MB**（旧方案写 12 个，有误） |
| 合计 | 51 个 / **392.8 MB** |
| 声道 | 单声道仅 1 条：`forest/day_3.ogg`（即 API 版 A1 会崩的那类） |
| 采样率 | 44.1 kHz 35 条、48 kHz 8 条 → 实例采样率必须取文件值 |
| 解码成本（100 ms 块） | P50 0.2–0.9 ms、P95 0.65–1.17 ms、max 6–9 ms |
| 预填 3 块 | 0.45–1.9 ms |
| 循环接缝 | 39 条 biome 中 **10 条** Δ > P99.5；最差 `snow/windy` **12254**（满量程 37%） |
| 交叉淡化效果 | 64 帧 → 321、256 帧 → 114（均 < P99.5） |
| 历史残留 | `biome/{forest,jungle}_{day,night}.ogg` 4 个文件代码零引用（21.4 MB 解码 / 2.3 MB 磁盘） |

### 11.4 已核实的 API 事实

| # | 事实 | 出处 |
|---|---|---|
| 1 | `BufferNeeded` 每 tick 泵一次、最多补 3 块；泵在 `DoUpdate` 之后；`Play()` 前必须预填 | FNA `FrameworkDispatcher.Update`；`Main.Update` 末尾 `((Game)this).Update(gameTime)` |
| 2 | `IsLooped` setter 是空实现，循环只能自己做 | FNA `DynamicSoundEffectInstance` |
| 3 | NVorbis 0.10.0.3 **没有** 4 参 `ReadSamples`（无上混）；`DecodedPosition` 已过时 | 反编译 `VorbisReader` |
| 4 | `.tmod` 内 `.ogg` 不参与 deflate，字节数 == 磁盘大小 | `TmodFile.ShouldCompress` |
| 5 | 读 `.tmod` 必须用独立句柄（`GetFileStream(..., true)`），共享流并发会抛异常 | `TModContentSource.OpenStream` |
| 6 | `Main.Update` 的 `try` 只包 `DoUpdate`；回调异常直穿 `Game.Tick` | `main-decomp.cs:16754–16791` |
| 7 | `ThreadCheck.CheckThread()` 非主线程抛 `ThreadStateException`；`Stop(immediate)` 在动态分支调用 | FNA `SoundEffectInstance.Stop` |
| 8 | `SoundEffect.Device()` 失败时抛 `NoAudioHardwareException`，不返回 null | FNA `SoundEffect.Device` |
| 9 | `Stop()` 在 `handle == IntPtr.Zero` 时提前返回，`ClearBuffers()` 不执行 → 预填未 Play 会漏原生内存 | FNA `SoundEffectInstance.Stop` |

### 11.5 定型的实现方向

1. **范围**：只流式化 43 条 biome 环境床；脚步 199、开箱/合成/NPC、97 个 wav 一次性音效保持常驻。
2. **不改 API 二进制**：平行类 `StreamedAmbience` 与 `ModAmbience` 同形，`AmbientHandler` 25 个字段换型，`Ambiences` 改 `List<IAmbience>`；自研流不注册进 `AllModAmbiences`。
3. **线程**：解码在工作线程，`SubmitBuffer` 只在主线程；3 × 100 ms 在飞；`BufferNeeded` 主路径 + 每 tick 兜底。
4. **循环**：回绕填充 + 256 帧等功率交叉淡化，统一开启。
5. **流池**：上限 4（含 1 条过渡位），按「静音最久」回收；`ChangeTrack` 命中 `byte[]` 缓存。
6. **三层兜底**：流内自愈 → 单条降级为常驻 `SoundEffect` → 配置开关全局关闭。

### 11.6 交付顺序

| 步 | 内容 | 验收 |
|---|---|---|
| S1 | `OggStream` 内核 + 离线回归（不接游戏） | 2.2 圈无丢帧、接缝 ≤ P99.5 |
| S2 | 单条打通：`SnowAggro` | 10 分钟 `Underruns == 0` |
| S3 | 流池 + 森林 4 条 | 常驻 ≤ 4 条、切群系无静音 |
| S4 | 其余 39 条 + 删预载 | 内存 369 MB → 约 10 MB |

### 11.7 排期

- 原估 15 人日 → 加兜底与观测后 **18.5 人日**；含风险缓冲约 **20–22 人日**。
- 可延后项：设备热切换（R7）、池容量调优（R13）。

### 11.8 待定事项

1. 流池上限取 4 还是 5（雨雪叠加场景）。
2. environment / rain 那 8 条是否纳入（只多省 23.6 MB，约 6%）。

### 11.9 下一动作

S1 开工前的唯一前置是上面两条待定项；确认后从 `OggStream` 内核与离线回归开始。

### 11.10 S1–S2 实施记录（2026-11-21 下午）

**已落地**：`Sounds/Streaming/` 下三个文件（`OggStream` / `BlockQueue` / `StreamedAmbience`，约 845 行），
外加四处接线（配置开关、`AmbientHandler` 双集合、卸载清理、调试面板）。详见 `docs/streaming-impl-2026-11-21.md`。

**三条修正了原方案的事实**：

1. **NVorbis 的 seek 只有位置 0 可靠。** `snow/windy` 上距末尾 4,096 帧以内必然错位（最大偏差 12,512）；
   `rain/light` 上连中点都错，因为该文件的 granule 元数据本身是坏的（`TotalSamples` 少报 448 帧）。
   原方案「打开时 seek 到 `total-W` 取尾部做交叉淡化」不可行；最终改为「只 seek 到 0，再顺序解码丢弃 W 帧」。
   尾部附近反复 seek 还会长时间卡死（`total-260` 超过 100 秒无返回）。
2. **API 内置的 `ModStreamedAmbience` 不能直接用。** 三个硬伤：`UpdateAll` 每帧 `Main.NewText` 刷屏；
   `Path.Combine(Mod.SourceFolder, …)` 在发布版为 null 会抛异常；`Volume` setter 内 `TryStartStreaming` 异常外抛。
   但它的块大小（1024 样本）、预填 4 块、`DynamicSoundEffectInstance` 用法被证实是对的，自研版沿用。
3. **`DynamicSoundEffectInstance.IsLooped` 是空实现**（getter 恒 false、setter 空），循环只能手动回绕；
   且 `Play()` 在 `State == Stopped` 时不会触发 `BufferNeeded`，预填必须早于 `Play()`。

**验收结果**：

| 项 | 结果 |
|---|---|
| 离线回归 | 51 / 51 通过（biome 43 + environment 5 + rain 3） |
| 逐帧一致性 | 渲染 3 个周期，与全量顺序解码的参考实现比对，**最大误差 0**（普通流与 tML 条目流语义下都是 0） |
| 原生会响的 9 条 | 全部修复，3.2×–48×；`snow/windy` 12,254 → 254，`hell` 1,713 → 65，`crimson` 2,201 → 94 |
| 解码吞吐 | 约 8,000 kframes/s；100 ms 块最大 3.6 ms |
| `BlockQueue` 并发 | 4 槽、20 万块，零丢失 / 零错序 / 零串块 |

**状态**：`SnowAggro` 已可由 `AmbientConfig.streamAmbientBeds` 切换到流式（默认关闭，改动需重载）。
开关关闭时 `1.4` 行为逐行不变。真机听感、设备热切换、长稳未验证，需人工。

**S3 已完成（同日下午）**：森林 4 条（Morning/Day/Evening/Night）迁移到流式，换曲走 `SetForestXTrack` 四个入口；
24 首森林曲目的预载改为「仅关闭流式时执行」，因此开启流式后不再付 198 MB。
真实客户端实测：`enabled, 5 streamed / 20 resident`；工作集同相位 A/B 为 2,287 MB → 1,370 MB，
其中约 236 MB 可由解码体积解释，其余差异来自分配器，未单独归因。

**打包已打通**：`dotnet build -p:BuildMod=false` 后用 `-eac` 复用 DLL 打包；目录须叫 `TerrariaAmbience`；
产物 63,981,018 B，服务器与独立客户端均加载通过。配方见 `docs/streaming-impl-2026-11-21.md` §9。

**S4 已完成**：25 条环境床全部改为 `StreamedAmbience`，`Ambiences` 收敛为单集合，
`ModAmbience` 在代码里零引用；关闭流式改为「全部走常驻兜底」而非保留第二套实现。
独立客户端实测：开启 1,107.5 MB / 关闭 1,778.4 MB（此前基线 2,286.9 MB），零异常。

**16:00 追加修复（主菜单双音乐）**：Alt-Tab 后主题曲与 tModLoader 自带 50 号曲同时响。
根因是 `MenuMusicGuard` 把失焦时的 `PauseAll`（FNA 暂停会冲刷动态声部队列）判成"坏死"，
叠了一层兜底曲且从不退场；重建路径还因 `Mod.GetFileNames()` 返回 null 抛 NRE。
已改为：失焦整段跳过、`IsPaused` 视为存活、兜底曲发现主题恢复即让位并停机、时间常数按秒计。
隔离环境用探针模组（自带菜单主题曲）模拟失焦/回焦验证：修复前 `fallback=True` 且日志 450 行/秒，
修复后 `slot50Playing=False`×3、`fallback=True` 0 次、日志 8 行。

**追加结论（18:45）**：「硫黄海/星辉地/硫火之崖/沉沦之海/霓虹场地无音乐」与本次改造无关。
探针显示状态为 `playing=True / fade=1.00 / pending=8 / realVol=0.444`，而隔离运行时播放同一首
`SunkenSea.ogg` 用户可听到；只关闭 `TerrariaOverhaul` 后音乐恢复。根因是 Overhaul 对
`Main.UpdateAudio` 的 ILHook（`EnableMusicPlaybackPositionPreservation`）会让 tML 动态音轨
"标志在播、样本流已断"。详见 `docs/streaming-impl-2026-11-21.md` §13。

**下一步**：真机听感与长稳验证；README 补配置说明。

### 11.11 打包（已打通）

`dotnet build -p:BuildMod=false` 之后用
`tModLoader.dll -build <源目录> -eac <源目录>/TerrariaAmbience.dll -unsafe true -savedirectory <隔离目录>`，
即可复用已编译 DLL 打包，产物 63.9 MB。直接 `-build` 会自己再编译一遍，那条路径上
`Krafs.Publicizer` 不生效，报 25 个 FNA 内部成员错误。目录名必须是 `TerrariaAmbience`（内部名取文件夹名）；
另注意 `-eac` 按**文件名在源目录下解析**，源目录里若躺着旧 DLL 会静默打进旧代码。

### 11.12 当日收尾（2026-11-21 19:20）

#### 提交

| 提交 | 内容 |
|---|---|
| `732ec24` | S1–S2：`OggStream` 解码内核、`BlockQueue` 四槽环形交接、`StreamedAmbience` 播放器 |
| `5a68066` | 流失败后退化为常驻 `SoundEffect`（第二层兜底） |
| `1cca97e` | 未起播即淡出时释放解码线程 |
| `2a87269` | 打包配方打通（`-eac` 复用已编译 DLL）+ 独立运行时实测记录 |
| `ce6a277` | S3：森林四条迁移 + 免预载 |
| `e197229` | S4：25 条全部迁移、`ModAmbience` 零引用、单集合 |
| `7986d2f` | 主菜单双音乐修复 |
| `7183252` `c986f2d` `2dfa51f` | `[AudioProbe]` 诊断（槽位名、真实时间节奏、FAudio 真实音量） |
| `86c6f99` | “部分群系无音乐”定位为 TerrariaOverhaul 冲突 |

#### 当日结论汇总

| 项 | 结果 |
|---|---|
| 环境床 | 25 条全部流式，关闭流式走同一条常驻兜底 |
| 工作集（主菜单） | 2,286.9 → **1,107.5 MB**（流式开启） / 1,778.4 MB（兜底） |
| 离线回归 | 51 / 51 通过，逐帧与参考实现误差 0 |
| 主菜单双音乐 | 已修复并复现验证（`fallback=True` 0 次、`slot50Playing=False`） |
| 部分群系无音乐 | **非本模组问题**：TerrariaOverhaul 的 `EnableMusicPlaybackPositionPreservation` |

#### 部署物（用户 `Mods` 目录）

| 文件 | 说明 |
|---|---|
| `TerrariaAmbience_stream.tmod` | 生效中，含流式 + 双音乐修复 + `[AudioProbe]`（18:08 构建） |
| `_versions\TerrariaAmbience_resident.tmod` | 无流式版（由 `1.4` 构建，**不含**双音乐修复） |
| `_versions\TerrariaAmbience_stream-20261121-*.tmod` | 各阶段快照，留作对照 |
| `_versions\TerrariaAmbience_old-20261121-0106.tmod` | 用户原包，未删 |

#### 遗留

1. **听感类验证**只能人工：森林昼夜换曲、雪原+雨、快速穿群系、退出世界回菜单。
2. **设备热切换 / 最小化恢复 / 30 分钟长稳**未测。
3. `_versions` 里的 resident 包由 `1.4` 构建，**不含** `7986d2f` 的菜单音乐修复；需要时另行出包。
4. 关掉 `streamAmbientBeds` 时森林 24 首仍会预载（这是兜底路径的前提），属预期。
