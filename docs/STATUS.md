# Terraria Ambience — 当前进度总览

> 最后更新：2026-11-21
> 分支：`1.4`（`origin` = `github.com/ora233-fantaea/TerrariaAmbience-fantasea`）
> 本文档是**入口**：先看这里了解现状，再按需跳转到各专项报告。

---

## 1. 一句话现状

代码不再有已知的崩溃、联机错位与服务端空引用问题；室内混响被修正到物理量纲；主菜单音乐在退出世界后不再消失。
**功能层面可正常游玩**，仍有若干素材层面与 API 层面的历史问题未处理，流式加载方案已完成设计但未实施。

---

## 2. 基线

| 项 | 值 |
|---|---|
| 模组版本 | `0.3.7.1`（`build.txt`，本轮未改版本号） |
| 上游 HEAD（改动前） | `77291bd`（2026-09-02） |
| 本轮起点 | `40e8727`（第一批 32 项修复） |
| 运行环境 | tModLoader 2026.9.3.1 / .NET 8 / FNA 1.0.0 / FAudio 22.9.1 |
| 依赖 | `TerrariaAmbienceAPI` 1.3.6.8（仓库内 `lib/` 的 DLL 与工坊 2025.10 版本**字节一致**） |
| 体量 | 433 个 tracked 文件 / 227.8 MB；音频 372 个（284 ogg + 88 wav）/ 磁盘 65.1 MB / **解码 447 MB** |

> 构建前置：`csproj` 导入 `..\tModLoader.targets`，因此**必须**把工程放在 `ModSources\<目录>` 下才能编译（见 §7）。

---

## 3. 改了什么

### 3.1 第一批：32 项缺陷修复（提交 `40e8727`）

20 个源码文件，`+2350 / −212`（含文档）。

| 组 | 内容 |
|---|---|
| **P0 崩溃 / 联机** | 联机包字段顺序；未知包不再抛异常、转发包补 `Send` 与来源校验；三处 `Main.dedServ` 守卫；`SoundInstance` 判空；两个从未真正发送的包 |
| **P1 泄漏与每 tick 开销** | 位置化音源先停旧再新建（止住原生 voice 泄漏）；滑冰/篝火音源静态持有并在卸载时释放；篝火检测改跨 tick 缓存（原本每 tick 扫 100×100 格）；找点/raycast/水深采样补前置概率门与判重；去掉每 tick 的 `List`/`Item` 分配；调试字典随之清理；FAudio 描述符改为复用并在卸载时释放 |
| **P2 逻辑与听感** | 雷声概率公式、合成节流、箱子双播与下标越界、像素/tile 混用、初始化标志写反、曲目随机开区间、低通起点终点重合、屋檐粘滞、水晶球分类缺失、BOSS 战音乐音量不回原值、水下升调、僵尸尺寸过滤恒真 |

未处理的两条：**B25 是误报**（`ContentSamples.NpcsByNetId` 的键就是 `SetDefaults` 收到的 type，不会 `KeyNotFoundException`）；**B27 无调用者**（`AStar` 死代码）。

### 3.2 第二批：混响修正、汉化、音乐守护（本次提交）

| 文件 | 改动 |
|---|---|
| `Sounds/SoundFilters/SoundFilterSystem.cs` | 混响参数**量纲归一**：房间表面数不再当作小数使用 |
| `Core/AmbientConfig.cs` | 新增 `reverbWetness`（0–1，默认 `0.3`），控制每个音源送入混响的量 |
| `Localization/zh-Hans_Mods.TerrariaAmbience.hjson` | **新增**简体中文配置界面（5 个面板 / 42 个字段 + 全部小节标题） |
| `Localization/en-US_Mods.TerrariaAmbience.hjson` | 同步补 `reverbWetness` 条目 |
| `Common/Systems/AudioTrackGuardSystem.cs` | **新增**：给 `LegacyAudioSystem.UpdateCommonTrack` 补判空，避免异常被 `Main.UpdateAudio` 的空 `catch` 吞掉后累计成永久静音 |
| `Common/Systems/MenuMusicGuard.cs` | **新增**：主菜单音乐守护（进菜单硬重启一次；真死才重建/兜底），并输出可判读的日志 |

---

## 4. 三个值得留档的机制结论

这三条是排查中花时间最多、也最容易被后人重踩的地方。

### 4.1 联机包字段顺序

发送侧写 `Int32 → Vector2 → String`，接收侧按 `Int32 → String → Vector2` 读。单机永远暴露不出来，联机时音效必然错位，长度前缀还可能被解析成大数。已修正为对称写入。

### 4.2 室内混响：把"表面数量"当成 0–1 的小数用

`GenerateAudioFilters` 原实现：

```csharp
DecayTime        = totalSurfaces * gain * 0.003f;
ReflectionsDelay = totalSurfaces / 16;
RoomSize         = totalSurfaces * gain * 0.5f;
```

`totalSurfaces` 是 flood fill 出的墙 + 方块总数（上限由 `reverbMaxArea = 2500` 控制）。一个 2500 格的房间会得到 **7.5 s 衰减 / 156 ms 预延迟 / RoomSize 1250**，而 FAudio 的 `RoomSize` 合法上限是 **100**。

听感表现为"硬地面上每一步都叠了一层东西"，且**只在室内**（室外 `ReverbGain ≈ 0`，湿声被静音）。修正为按 `roomScale = clamp(surfaces/2000, 0, 1) × gain` 归一：

| 参数 | 修正后 |
|---|---|
| `DecayTime` | `0.2 + 1.8 × roomScale`（≤ 2.0 s） |
| `ReflectionsDelay` | `4 + 24 × roomScale` ms（≤ 28 ms） |
| `ReverbDelay` | `8 + 32 × roomScale` ms（注意此字段是 **byte**） |
| `RoomSize` | `100 × roomScale`（不越界） |
| 送量 `ReverbGain` | `gain × reverbWetness`（默认 0.3） |

### 4.3 主菜单音乐在退出世界后消失

完整链条（全部有反编译依据）：

1. `Main.UpdateAudio` 的整个 try 块只有一个空 `catch`，里面做 `musicError++`；**累计 100 帧**后直接 `musicVolume = 0f`，而 `musicVolume == 0` 会让下一帧的 `curMusic` 恒为 0 —— 音乐永久静音且不打印任何日志。
2. 只有 **50 / 51**（`MusicID.MenuMusic`，XACT **Cue** 音轨）被 tML 特殊照顾（`musicFade[50] = 1f` + 常驻 51），所以 `tModLoader` 与 `Journey's End` 两个主题从不掉音；其余主题用的是 `OGGAudioTrack` / `MP3AudioTrack`，走 `DynamicSoundEffectInstance`。
3. FNA 的 `SoundEffectInstance.State` 里有一段自检：`BuffersQueued == 0` 就判为停止 —— 但带 `!isDynamic` 条件，**动态实例被跳过**。所以这类声部即使一个样本都放不出来，`IsPlaying` 依旧返回 `true`，不能用它判断有没有声音。
4. `IAudioTrack.Reuse()` 只把解码位置拨回开头，**不清空已排队的缓冲**。因此对处于异常队列状态的声部做 `Reuse + Play` 等于什么都没做 —— 这就是"守护写了却没用"的原因。
5. 正确顺序是 **`Stop(Immediate) → Reuse → SetVariable("Volume") → Play`**：先冲刷队列，再重建播放。

`MenuMusicGuard` 因此只在**进入菜单/换主题时**对 SE 类音轨做一次硬重启，之后不再干预；只有当句柄为空、已释放、或 `pending` 与 `queued` 同时为 0 时才重建一次，仍失败才回退到 50 号。

> 反例留档：曾经用"`PendingBufferCount` 长时间不变"判停滞，这是错的。`ASoundEffectBasedAudioTrack.Update()` 每帧在 `< 8` 时补两块，**健康播放时该计数几乎恒为 8**。误判会导致每 0.33 秒重启一次健康曲目（听感为"播 1–2 秒又重头开始"），并在若干次后错误触发兜底换曲。

---

## 5. 验证记录

| 项 | 结果 |
|---|---|
| 编译 | **0 错误**，7 条警告（5 条既有） |
| 打包 | `64,056,578 B`，SHA256 `4f97ae9b…` |
| 产物自证 | 包内 DLL 与构建输出哈希一致；不含任何测试插桩（`QaHookProbe` / `QA-PROBE`） |
| 服务器加载 | 通过：`Adding Content → Finalizing Content → Adding Recipes`，模组自身零 Error/Exception |
| 服务器建世界 | 通过：4200×1200 生成完毕、`Listening on port 7777`、`Server started` |
| `dedServ` 守卫 | 插桩实测 `[QA-PROBE] AudioLoopsSystem.OnModLoad dedServ=True` |
| 本地化 | 用 tML 自带 `Hjson.dll` 实际解析：`PARSE OK`，键集合与 en-US 一致（42 字段） |
| 客户端实测 | 用户确认：室内混响叠加感消失；退出世界后主菜单音乐正常播放 |
| 隔离自证 | 全流程仅使用私有 `savedir` / `modpath` / 空 `steamworkshopfolder`；用户 `config.json`、`enabled.json` 哈希前后一致 |

---

## 6. 仍未解决 / 未验证

### 6.1 代码层面（明确知道，但未改）

| 项 | 位置 | 说明 |
|---|---|---|
| NPC 脚步默认静音 | `Core/AmbientConfig.cs:23` | `float entityFootstepsVolume` 上标注 `[DefaultValue(true)]`，tML 无法把 `bool` 转成 `float`，默认值失效、字段保持 0 → 该功能恒为 0。改为 `1f` 会**改变默认听感**，需确认后再动 |
| 三种方块双击 | `Content/AmbientAndMore/FootstepHandler.cs` | `StoneBlocks ∩ SmoothStones = { Asphalt, Sunplate, Titanstone }`，这些方块每一步会同时播两层、音量相同 |

### 6.2 素材层面

- 石质脚步 8 个变体中有 **4 个自带 20–40 ms 的第二次撞击**（step3/4/6/8），落在 flam 区间，会被听成"一下半"。改代码解决不了，只能重制或替换素材。
- `light1..6`（时装脚步）中 `light4/5/6` 与 `light1/2/3` **字节相同**，实际只有 3 个变体。

### 6.3 依赖程序集（`TerrariaAmbienceAPI` 1.3.6.8）

仓库只有 DLL，没有工程文件，因此 **A1–A12 十二条未修**。详见 `docs/terraria-ambienceapi-analysis.md`。其中影响最大的：

- **A1** `ModStreamedAmbience` 对单声道 Ogg 必然越界（缓冲 2048，单声道上混写 `dst[4095]`）；主模组**零引用**，属潜伏缺陷
- **A2** `UpdateAll` 里裸 `Main.NewText` → 每条流式音轨每帧一条聊天消息
- **A6** `CachedAmbiences` 不随卸载清理，键不含模组名 → 跨模组同名路径会 `Add` 抛异常

### 6.4 需要真实会话才能量化的项

- B15 / B16 修复后的实际触发频率（单位、初始化标志）
- 各类脚步、动物叫声的听感平衡
- FNA voice 数量随游玩时长的变化（验证泄漏是否彻底止住）

### 6.5 内存与流式

解码内存实测 **447 MB**（不是早期估算的 997 MB），其中 **43 条 biome 环境床占 369 MB（82%）**，森林 24 个文件就占 198 MB。`InitializeAllAmbienceToAvoidRuntimeOverhead()` 在加载阶段把 24 首森林曲目全部请求，而一局最多播 2 首，约 **180 MB 是纯浪费**。

方案已写好但**未实施**：`docs/streaming-plan-2026-11-19.md`（P1 删预加载 249 MB → P2 懒加载 105 MB → P3 流式内核 → P4 流池 ≤4 → P5 全量 78 MB）。核心约束与已验证的 FNA 事实都记在那份文档里。

2026-11-21 在 `stream-1.4` 分支完成**可行性验证**（`docs/streaming-verification-2026-11-21.md`），三条结论会改变实现：
NVorbis 无「上混」重载（按文件声道数建实例即可，单声道原生支持）；`BufferNeeded` 确实每 tick 泵一次、但泵在 `DoUpdate` 之后且要求 `Play()` 前预填；
39 条 biome 里 **10 条** 原样回绕会有可闻接缝（最差 `snow/windy` Δ=12254），交叉淡化是必需项而非保险，建议 256 帧。

---

## 7. 构建与验证方法（可复现）

```bash
# 1. 准备隔离运行时（不写游戏安装目录）
#    复制 tModLoader.dll / .deps.json / .runtimeconfig*.json
#    用 junction 链接 Content 与 Libraries
#    在 ModSources 下写一个导入真实 tMLMod.targets 的 tModLoader.targets 垫片

# 2. 同步源码到 ModSources 并编译
cp -r Common Content Core Helpers Localization Properties Sounds lib <iso>/ModSources/TerrariaAmbience/
cp TerrariaAmbience.cs TerrariaAmbience.csproj build.txt description.txt icon.png <iso>/ModSources/TerrariaAmbience/
dotnet build TerrariaAmbience.csproj -c Release -p:BuildMod=false

# 3. 打包（不重新编译，直接复用刚构建的 DLL）
dotnet tModLoader.dll -build "<abs>/ModSources/TerrariaAmbience" \
  -eac "<abs>/ModSources/TerrariaAmbience/bin/Release/net8.0/TerrariaAmbience.dll" \
  -unsafe true -savedirectory "<abs>/savedir"

# 4. 服务器加载冒烟（工作目录必须是 iso，否则找不到 ReLogic.dll）
dotnet tModLoader.dll -server -nosteam -savedirectory "<abs>/smoke" -modpath "<abs>/smoke/Mods"
# 期望看到：Adding Content: Terraria Ambience → Finalizing → Adding Recipes
```

安装到游戏：把 `.tmod` 放进 `Documents\My Games\Terraria\tModLoader\Mods\`。tML 对同名模组**本地优先于工坊**，日志里会显示 `Selected TerrariaAmbience 0.3.7.1 from Local`。

---

## 8. 产物与文档索引

| 路径 | 内容 |
|---|---|
| `docs/progress.md` | 原始缺陷台账（B1–B31，含逐条行号与修法） |
| `docs/qa-report-2026-11-18.md` | 静默 QA 的问题清单（35 条，按严重度排序，含 B25/B12 的更正） |
| `docs/terraria-ambienceapi-analysis.md` | `TerrariaAmbienceAPI` 完整拆解（版本谱系、三类基元、耦合方式、A1–A12） |
| `docs/fix-report-2026-11-19.md` | 第一批修复的逐条说明与验证，§8 记录室内脚步震颤的诊断链 |
| `docs/streaming-plan-2026-11-19.md` | 流式改造的分阶段方案与 FNA/NVorbis 事实核查 |
| `docs/streaming-verification-2026-11-21.md` | 流式可行性验证：实机解码、接缝量化、API 修正清单 |
| `docs/STATUS.md` | 本文件 |
| `dist/*.tmod` | 构建产物（**已在 `.gitignore` 中**，不进库） |
| `.home/qa/**` | 隔离测试现场（**已忽略**，可整目录删除，不影响仓库与游戏） |

---

## 9. 建议的后续顺序

1. **先做一次长时间实机**（30 分钟以上）：确认 voice 不再累积、菜单音乐在多次进出世界后仍正常。这条只有玩家能提供。
2. `entityFootstepsVolume` 的默认值（一行，但会改变默认听感，需拍板）。
3. 三种方块的双播（给 `StoneBlocks`/`SmoothStones` 去交集，或在匹配时取首个命中）。
4. 素材重制（石质 4/8 变体的二次撞击、时装的 3 组重复文件）。
5. 内存：先做 P1（删预加载，约 1 小时、完全可回退），再决定是否投入流式。
6. 若要长期维护：把 `ModAmbience` / `FootstepSound` / `PlayerTileChecker` 内联（约 350 行），可一并消掉空引用、菜单静音失效、每帧 O(n²) 文本测量，并让 25 条音轨按需加载。
