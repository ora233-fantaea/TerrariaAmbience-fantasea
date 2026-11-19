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
