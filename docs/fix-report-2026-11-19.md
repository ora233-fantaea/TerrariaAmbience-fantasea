# Terraria Ambience · 修复报告

| 项 | 值 |
|---|---|
| 日期 | 2026-11-19 |
| 基线 | 分支 `1.4`，HEAD `77291bd`（工作区未提交改动 20 个 `.cs`） |
| 运行时 | tModLoader `1.4.4.9+2026.09.3.1` / .NET 8.0.426 |
| 范围 | 主模组仓库内的全部已确认缺陷；`TerrariaAmbienceAPI` 是独立程序集，不在本仓库内 |
| 结果 | 编译 0 错误；服务器完整加载；见 §4 验证 |

---

## 0. 总览

| 分类 | 数量 | 说明 |
|---|---|---|
| 已修复 | 32 | 台账 B1–B24、B26、B28–B31 与新增 N1–N9 |
| 判定为误报，不改 | 1 | B25（`ContentSamples.NpcsByNetId` 的键就是 type） |
| 死代码，不改 | 1 | B27（`AStar` 无任何调用者） |
| 第三方程序集，无法在本仓库修复 | 12 | API 侧 A1–A12，仅 A4 的影响已在本仓库侧加了守卫（N1） |

> B12 只修前半句（节流被前置赋值废掉）；后半句 `createWall >= 0` 已证伪，保持原样。

---

## 1. 崩溃与联机（P0）

| ID | 位置 | 改动 |
|---|---|---|
| **B20** | `TerrariaAmbience.HandlePacket` / `TAPID.SEND_CRAFT` | 服务端转发改为先读 `String` 再读 `Vector2`，与客户端读取顺序一致；不再把位置的前 4 字节当字符串长度 |
| **B21** | 同上 / `default` 分支、`TAPID.SEND_AMB_SFX` | 未知包改为 `Logger.Warn` + `return`；转发前校验路径前缀 `TerrariaAmbience/Sounds/`，并按客户端限速 30 条/秒 |
| **B22** | `SEND_AM_AMB` 客户端分支 | `curEveningAmb/curNightAmb` → `curMorningAmb/curDayAmb`（复制粘贴写错） |
| **B23** | `AudioLoopsSystem.PreSaveAndQuit`、`FilterHooks.PreSaveAndQuit`、`RemoveDefaultWindAndRainSystem.Unload` | 三处补 `if (Main.dedServ) return;`，音源访问改 `?.` |
| **B24** | `AudioLoopsSystem.OnModLoad`、`TerrariaAmbience.Load` | 服务端不再构造 `PositionalAudio2D`（实参求值即触发 `ImmediateLoad`），也不再 `Request` 24 条森林音轨 |
| **N1** | `AmbientHandler.Update`（两处） | `SoundInstance` 是 API 的合法 `null` 状态（资源缺失时会显式赋 null），改为判空后再设 `Pitch` |
| **N8** | `SEND_AM_AMB` / `SEND_PM_AMB` 服务端分支 | 原先只 `Write` 不 `Send`，昼夜曲目在联机里从未真正同步；补 `p.Send(ignoreClient: whoAmI)` |
| **N9** | `AmbientPlayer.HandleIceScraping` | 静态音源只允许本地玩家驱动；原先多人游戏里会被其他玩家的 `slippy` 覆盖 |

## 2. 资源泄漏与每 tick 开销（P1）

| ID | 位置 | 改动 |
|---|---|---|
| **B1** | `PositionalAudio2D.Play` | 新建实例前 `Stop()` + `Dispose()` 旧实例；新增 `ReleaseAllInstances()`（世界卸载）与 `DisposeAll()`（模组卸载） |
| **B2** | `AmbientPlayer` | `_areSoundsInitialized` 提升为静态；新增 `DisposeIceSounds()`，在 `OnWorldUnload` 与 `Unload` 调用 |
| **B3** | `FilterHooks.PostUpdateEverything` | 倒序遍历并剔除已停止/已释放的流式音源，`_oldLowPasses` 同步剪枝 |
| **B4** | `AmbientPlayer.GetCampfireCache` | 100×100 方框扫描改为「跨 tile 或满 30 tick」才重算，结果缓存复用 |
| **B5** | `AudioLoopsSystem.PostUpdateEverything` | 概率门提前到找点之前，找点结果存局部变量；补世界边界判定，避免 `Main.tile` 越界 |
| **B6** | `ImmersiveSoundsSystem` | 两个 `List<Vector2>` 改为静态缓冲，进入时清空 |
| **B7** | `SoundFilterSystem.UpdateReverbParams` | 听者未跨 tile 且房间格数不变时跳过重算；raycast 路径下每轮要为每格走一条 Bresenham 线，这一判重省掉绝大多数重复计算 |
| **B8** | `GeneralHelpers.GetWaterPressureFloat` | 逐像素采样改为 8px 步长（480 次 → 60 次） |
| **B9** | `CheckPlayerArmorSlot`、`FootstepHandler.Update` | 去掉每 tick 6 次 `new Item()`；两趟遍历合并为一趟，消除每 tick 分配的闭包 |
| **B10** | `TileSoundsPlayer` | 不再为 1600 个格子分配 `List<Point>`，改双层循环直扫并加世界边界判断 |
| **B26** | `TileVisualizationHelpers` | 关闭调试时清空 `TileVisualizations`（原先只增不减）；`Unload` 释放 1×1 调试纹理 |
| **B28** | `FilterHelpers` | 四类滤波失败改为「仅在开启提示时记录一次」，不再每 tick 刷聊天框 |
| **B30** | `FAudioReverbController.ReverbAttach` | 发送描述符由「每音源 Malloc 一次」改为 `RvInit` 里分配一次并复用 |
| **B31** | 同上 | 新增 `Shutdown()` 释放描述符并在模组卸载时调用；`Unload` 不再因 `DefaultAmbientHandler == null` 整体跳过释放 |

## 3. 逻辑与听感（P1/P2）

| ID | 位置 | 改动 |
|---|---|---|
| **B11** | `ImmersiveSoundsSystem` | `NextFloat(1f / cloudAlpha)` → 直接按 `0.005 * cloudAlpha` 判定，无雨时不再得到 `+∞` |
| **B12** | `CraftSounds.OnCreated` | 先保存 `TimeSinceLastCraft` 再清零，`> 60` 不再恒假 |
| **B13** | `AmbientPlayer.ManageChestSounds` | 「关闭」与「切换箱子」合并为一条路径（只响一次）；`Main.chest[i]` 全部经 `TryGetChestCenter` 做越界与空引用保护；猪猪银行/保险箱不再让状态卡死 |
| **B14** | `AmbientPlayer.GetArmorStepVolume/GetVanityStepVolume` | 头盔加成由 `*= 1.3f`（乘在 0 上）改为 `+= 0.3f` / `+= 0.35f` |
| **B15** | `AmbientHandler`（Hell 条件） | `position.Y`（像素）与 `Main.rockLayer`（tile）比较改为 `> Main.rockLayer * 16` |
| **B16** | `SyncAmbienceSystem.PostUpdateEverything` | 初始化标志改为「进入世界首帧执行一次、回菜单重置」，不再在主菜单每帧换曲 |
| **B17** | `InventorySoundsGlobalItem` | `item?.type <= 0` 改为 `item is null \|\| item.type <= 0`；`_prevItem` 由静态改为实例；远端玩家只处理 `HeldItem` |
| **B18** | `SlimeSounds` | 落地与入水音量改用 `Math.Abs(velocity.Y)`；音量兜底顺序改正；服务端不再播放 |
| **B19** | `SoundFilterSystem.CalculateLowPass` | 起点==终点时短路返回 1.0/未启用；全局参数不再做一次注定为空的线扫 |
| **B29** | `FilterHooks.ActiveSound_Play` | 去掉 `orig(self)` 之后多余的 `Play()` |
| **N2** | `AmbientPlayer.HandleWetSteps` | 每 tick 先置 `false`，找到实心块才置 `true` 并 `break` |
| **N3** | `AmbientHandler.Initialize` | 四个初始曲目 `Next(1, N)` → `Next(1, N + 1)`，第 6 轨可选 |
| **N4** | `CraftSounds.AllCategories` | 补入 `Magical`，水晶球合成不再被 `wasValidStation` 挡掉 |
| **N5** | `TerrariaAmbience.Main_Update` | 首次启用时记录 `Main.musicVolume`，关闭该选项时还原 |
| **N6** | `NPCFootstepHandler` | `frame.Height < 150`（样本 NPC 未跑 `FindFrame`，恒为 0）改为 `height` 判定；字典改用索引器，重复类型不再抛异常 |
| **N7** | `AmbientHandler.Update`（水下分支） | `Pitch -= -0.05f`（等价升调，且依赖上一轮归零）改为 `Pitch = -0.05f` |

---

## 4. 验证

| 层 | 结果 |
|---|---|
| 编译 | **0 错误**，7 条警告（其中 5 条为既有：`CS0169`/`CS0414`/2×`ChangeMagicNumberToID`/`CS0436`，另 2 条为 `CS0672` 与其中文重复） |
| 打包 | 成功，`63,961,578 B`，SHA256 `46bafbd0…`（修复前 `63,959,853 B` / `e42d89c2…`） |
| 产物自检 | 从 `.tmod` 中确认不含任何探针符号（`QaHookProbe` / `QA-PROBE` 均为 False） |
| 服务器加载 | 通过：`Finalizing Content → Adding Recipes`，`[TerrariaAmbienceAPI]: Loaded 0 ModAmbience(s).`，模组自身无 Error/Exception |
| `dedServ` 守卫 | 实测确认：`[QA-PROBE] AudioLoopsSystem.OnModLoad dedServ=True`（插桩仅存在于隔离副本，已删除） |
| 服务端内存 | tML 自报 444 MB 物理 / 504 MB 虚拟（修复前的世界测试曾报道 19.2 GB 提交、可用 −3.3 GB） |
| 建世界 | **通过**：小世界（4200×1200）生成完毕、`Listening on port 7777`、`Server started` |
| 保存退出 | **未能端到端驱动**，见 §5 |
| 客户端听感 | **未验证**（本机隐藏桌面下客户端仍在加载阶段停滞，零模组对照同样停滞，属环境限制） |

### 需要更正的旧结论

`Mods using the most RAM: TerrariaAmbience 25.9 MB` **不是** B24 的证据。修复后该数字仍为 25.8 MB，而插桩已证明 `dedServ` 守卫确实执行——说明它是 tML 的资源/文件统计口径，不表示"已解码进内存的音频"。

---

## 5. 保存退出（B23）为什么只做到"半端到端"

世界能建、能加载、能监听端口，但**触发一次真正的保存退出没有成功**，原因逐条记录如下：

1. 管道输入 `printf 'exit\n'`：日志出现 `: Invalid command.`，命令未被识别。
2. `-exit` 启动参数：无效，进程持续运行。
3. Python 持有 stdin 管道、在 `Server started` 后连写 6 次 `exit`：服务器完全没有反应。
4. 直接原因见日志：`[Server Input Thread/WARN] [tML]: Console input not connected, ignoring...`
   —— 在无控制台句柄的环境下，tML 的服务端输入线程直接放弃读取 stdin。
5. 同一原因还导致 `Main.DedServ_PostModLoad` 里的 `Console.Clear()` 抛 `IOException: 句柄无效`
   （被 tML 以 `Silently Caught Exception` 吞掉，与模组无关）。
6. 追加的 A/B 探针也没能给出结果：该实例的 `PostUpdateEverything` 在 120 秒内一次都没被调用，
   说明无控制台的服务器没有推进到 ModSystem 的 tick 阶段。

因此 B23 的结论建立在两条确定性证据上，而不是端到端回放：

- **实测**：服务端模组加载阶段 `Main.dedServ == true`（`[QA-PROBE] ... dedServ=True`）。
- **代码**：三个钩子的第一条语句都是 `if (Main.dedServ) return;`，其余访问全部改用 `?.`。
- **反证**：旧的 `DefaultAmbientHandler.RandomizeDawnTracks()` 在服务端必然空引用——
  `PostSetupContent` 构造了 Handler，但 `Initialize()` 在 `dedServ` 下提前返回，
  `ForestDay` 等 25 个字段全为 `null`。

需要补的一次验证：在有真实控制台的机器上启动服务器，进图后输入 `exit`，确认 `server.log` 无
`NullReferenceException`。这一步在当前无控制台的环境里无法完成。

---

## 6. 玩家可感知的行为变化

1. **联机**：其他玩家合成会正常播放对应音效（此前必错位）；昼夜森林曲目真正同步。
2. **主菜单**：不再每帧换曲（此前 `SyncAmbienceSystem` 在菜单里每 tick 调 `ChangeTrack`）。
3. **水面**：水下所有环境音的音高改为降低 0.05（此前是升高）。
4. **音乐音量**：关闭 `bossFightMusicAdjust` 后会还原为开启前的值，不再永久停留在 0.2。
5. **箱子**：关闭箱子只响一次；猪猪银行/保险箱不再让后续箱子音效失灵。
6. **雨声脚步**：走过一次屋檐后不会永久失效。
7. **水晶球**：合成时终于有声音。
8. **第 6 轨**：6 号森林曲目可以正常被抽到。
9. **僵尸脚步**：体型过滤改为真实尺寸判定（此前恒不过滤）；大型变体不再注册。

---

## 7. 未修复与未验证

- **B25**：误报。`Terraria.ID.ContentSamples.NpcsByNetId` 的键就是 `SetDefaults` 收到的 type，不是 netID。
- **B27**：`Common/AStar.cs` 无任何调用者，属死代码，删除与否不影响运行；本轮未动。
- **API 侧 A1–A12**：位于 `lib/TerrariaAmbienceAPI.dll`（独立模组 `2826615136`）。修它等于 fork 第三方依赖，不在"修本仓库 bug"的范围内。其中 A4（`ChangeTrack` 假定实例非空）已在本仓库侧用 N1 的判空兜住；A1/A2 只影响 `ModStreamedAmbience`，主模组零引用。
- **听感与频率**：B15/B16/B19/B2 修复后的实际听感、FNA voice 数量变化，需要一次真实进世界的会话才能确认。

---

## 8. 待验：室内走路出现"震颤 + 电流音"（2026-11-19 记录，未动手）

**现象**：室内行走时可听到类似"系统提示音颤抖、夹杂电流"的质感。

### 8.1 机制已量化

`Sounds/Custom/steps/armor/heavy1..9` 是 **32 kHz 单声道、每段 801 ms** 的采样，而行走步频约 350 ms。
tML 的 `SoundStyle` 默认 `MaxInstances = 1`、`SoundLimitBehavior = ReplaceOldest`、`LimitsArePerVariant = false`；
但 API 的 `FootstepSound.PlayAny` 是 `SoundEngine.PlaySound(ref SoundStyles[Main.rand.Next(n)], ...)`
—— **每个变体预建了独立的 `SoundStyle`**，于是这份"最多 1 个"的预算按变体各算一份，不是整组共用。

结果（9 个变体、350 ms 步频、801 ms 采样，相邻两步必然重叠）：

| 情形 | 概率 | 听感 |
|---|---:|---|
| 相邻两步抽到不同变体 | 8/9 ≈ 89% | 两段金属衰减尾叠在一起 → 拍频/梳状滤波 → "颤" |
| 相邻两步抽到同一变体 | 1/9 ≈ 11% | `ReplaceOldest` 在非零振幅处硬切旧实例 → "咔" |

### 8.2 三条待验假设（按成本排序）

| # | 假设 | 验证成本 |
|---|---|---|
| H1 | `ActiveSound_Play` 中 `SetOutputVoices` 现在作用在**已发声**的 voice 上（本轮删掉重复 `Play()` 的副作用，见下） | 改 3 行，10 分钟 |
| H2 | 盔甲/时装/地形三层 `UseAnyTile = true` 同时匹配，与 8.1 的变体叠加共同作用 | 脱掉头盔胸甲走两步，零改动 |
| H3 | 室内 `GenerateAudioFilters(PlayerRoom)` 的混响参数在小房间下过强，形成金属余响 | 关掉 reverb 开关对比 |

### 8.3 H1 的现成补丁（未应用）

当前 `Sounds/SoundFilters/FilterHooks.cs` 的顺序是先播再挂滤波：

```csharp
orig(self);                             // 已经出声
var vol = self.Sound.Volume;
self.Sound.Volume = 0;
ActiveSoundFilter(self, self.Sound);    // 内部 SetOutputVoices
self.Sound.Volume = vol;
```

原版此处结尾还有一句 `self.Sound.Play()` 重启播放（B29 的"双重播放"），它虽然多余，却让路由切换发生在重新起播之前、瞬态被掩盖。删掉后瞬态暴露。
若 H1 成立，改为先挂滤波再起播即可（voice 尚未发声，切换路由无害，且无需归零音量）：

```csharp
ActiveSoundFilter(self, self.Sound);
orig(self);
```

### 8.4 若 H1/H2 都不成立，再考虑探测 mod

设计要点已讨论，不落库：四个通道 —— T1 分层静音（热键逐层关，靠耳朵二分）、T2 播放账本（tick/路径/音量/音高）、
T3 同路径存活实例计数与截断次数（把"像叠加"变成"第 N 帧确有 2 个实例"）、T4 `FAudioVoice_GetState` 在 attach 时的返回值与每帧参数 Δ。
硬约束：采集须纯读无副作用、环形缓冲退出时一次落盘、报告直接给结论。
注意 `PlayAny` 会改写预建 `SoundStyle` 的 `Volume` 字段，属共享可变状态，探测时不要并发读写。
