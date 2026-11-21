# Terraria Ambience（中文说明）

给泰拉瑞亚世界加"人味"的氛围音模组：森林虫鸣、营火噼啪、丛林蛙声、海浪、腐化与猩红的低吼、
地下与地狱的轰鸣、雪原的风、按方块材质区分的脚步声、地牢骨响，以及新的雨声、水声与岩浆声。
另含室内混响、遮挡闷音与水下音染。

- 模组版本：`0.3.7.1`（`build.txt`）
- 上游作者：**RighteousRyan**（[论坛帖](https://forums.terraria.org/index.php?threads/terraria-ambience-mod.104161/)）
- 本仓库：`ora233-fantaea/TerrariaAmbience-fantasea`（fork）
- 运行依赖：**TerrariaAmbienceAPI**（仓库 `lib/` 内带编译用 DLL，游戏里仍需安装该模组）
- 实测环境：tModLoader 1.4.4 · `2026.9.3.1`

> 英文原版说明保留在 [`README.en.md`](README.en.md)。

---

## 一、两个分支

| 分支 | 说明 |
|---|---|
| `1.4` | 与上游同步的稳定版。环境床在加载时整块解码并常驻内存。 |
| `stream-1.4` | 本 fork 的流式改造线：环境床改为**边解码边播放**，并修复了主菜单音乐守护。两条线**不合并、不回灌**。 |

本 README 描述的是 `stream-1.4`。

## 二、这个分支改了什么

### 1. 环境床流式化

原来 25 条环境床（群系 / 天气 / 环境）会把整首 OGG 解码进内存：

- 43 个群系音频解码后共 **369.2 MB**，加上 environment 与 rain 共 51 个文件 **392.8 MB**；
- 引入流式后，单条流常驻约 **75 KB**（48 kHz 立体声、4 块 × 100 ms），最多 8 条并发 ≈ **0.6 MB**。

实测工作集（主菜单，同一台机器、同一个包，只改一个开关）：

| 配置 | 工作集 |
|---|---|
| 改造前（常驻解码） | 2,286.9 MB |
| 流式关闭（走常驻兜底） | 1,778.4 MB |
| **流式开启（默认）** | **1,107.5 MB** |

### 2. 循环接缝处理

这些环境音首尾并不严丝合缝，原样回绕会"咔"一声。对 51 个文件逐个量化后，**9 条**的回绕跳变超过
素材自身的 P99.9，最差 `snow/windy` 达 12,254（满量程 37%）。现在统一做 256 帧等功率交叉淡化：

| 音轨 | 原生回绕 | 淡化后 |
|---|---|---|
| `snow/windy` | 12,254 | **254** |
| `crimson/rumbles` | 2,201 | 94 |
| `hell/rumbles` | 1,713 | 65 |

离线回归 **51 / 51 通过**；与"整首顺序解码"的参考实现逐帧比对，**最大误差 0**。

### 3. 主菜单音乐守护修复

失焦（Alt-Tab）再回来后，主题曲与 tModLoader 自带菜单曲会同时响。根因是把原版"失焦暂停"
误判成音轨坏死，于是叠了一层兜底曲且从不退场。已改为：失焦期间不做判定、`IsPaused` 视为存活、
兜底曲一旦发现主题恢复立刻让位、时间常数由帧改为秒。

### 4. 诊断工具

打开 `debugInterface` 后：

- 屏幕右上角列出 25 条环境床的**音量**与**欠载计数** `(uN)`，正常应恒定 `u0`；
- 日志周期性输出 `[AudioProbe]`：`cur/new/fade/track/playing/paused/name/dynVol/realVol/pending/queued/rate/ch`。

排查"某个群系没有音乐"时按这个顺序读：

| 现象 | 含义 |
|---|---|
| `new=0` | 没有判定出曲目（群系 / SceneEffect 侧） |
| `track=NULL` | 槽位注册了但音轨没建起来（音乐包侧） |
| `pending=0` | 解码没产出数据 |
| `realVol=0` | 声部被外部静音 |
| 全部正常却无声 | 查第三方音频接管（已确认 TerrariaOverhaul 会如此） |

---

## 三、配置项

模组配置 → **Terraria Ambience**：

| 项 | 默认 | 说明 |
|---|---|---|
| `streamAmbientBeds` | `true` | 环境床流式开关，**改动后必须重载模组**。关闭不换实现，而是让所有音轨走"常驻兜底"，等价 `1.4` 行为。 |
| `debugInterface` | `false` | 调试面板 + `[AudioProbe]` 日志，可随时开关。 |
| `showInfoAndWarnings` | — | 聊天栏提示。 |
| 各群系音量 | — | `forestVolumes` / `snowVolume` / `jungleVolumes` / `evilVolumes` / `desertVolume` / `oceanVolume` / `cavernsVolume` 等。 |

其余（脚步、营火、武器音、混响）沿用上游配置。

---

## 四、从源码构建

来源：`TerrariaAmbience.csproj` 第一行 `<Import Project="..\tModLoader.targets" />`，
因此源码**必须放在 `ModSources` 下**才能构建，且目录名必须是 `TerrariaAmbience`
（tML 按文件夹名匹配 csproj，内部名也取自它）。

```bat
:: 1) 只编译，不打包
dotnet build TerrariaAmbience.csproj -c Release -p:BuildMod=false

:: 2) 复用已编译 DLL 打包（-eac）
dotnet tModLoader.dll -build "<源目录>" ^
    -eac "<源目录>\TerrariaAmbience.dll" ^
    -unsafe true -savedirectory "<隔离存档目录>"
```

三个坑，踩过：

1. **不要直接 `-build`**。它会自己再编译一遍，那条路径上 `Krafs.Publicizer` 不生效，
   会报二十多个 FNA 内部成员不可访问的错误（全部落在未改动的文件里）。
2. **`-eac` 按文件名在源目录下解析**。源目录根部若躺着一份旧 DLL，会静默打进旧代码。
3. 打包前先关掉正在运行的游戏，否则 `.tmod` 被占用。

产物约 63.9 MB（含全部音频）。

---

## 五、目录结构

```
Common/Systems/        主菜单音乐守护、音轨判空补丁、移除原版风雨声
Content/               AmbientHandler（25 条环境床的定义与每帧包络）、玩家与滤镜等
Core/                  AmbientConfig（配置）、MethodDetours（调试面板）、物品与配方
Sounds/Streaming/      OggStream（解码内核）、BlockQueue（四槽环形交接）、StreamedAmbience（播放器）
Sounds/SoundFilters/   混响与滤波（FAudio 后门）
docs/                  设计与验证文档，见下
lib/                   TerrariaAmbienceAPI.dll（编译期）
```

## 六、文档

| 文件 | 内容 |
|---|---|
| `docs/streaming-impl-2026-11-21.md` | 流式实施记录：S1–S4、验收数据、打包配方、双音乐修复、无音乐问题的定位 |
| `docs/streaming-verification-2026-11-21.md` | 可行性验证：实机解码、接缝量化、对原方案的修正 |
| `docs/streaming-contingency-plan-2026-11-21.md` | 风险登记表与三层兜底预案 |
| `docs/progress.md` | 缺陷台账（上游 1.4）+ 本分支进度 |
| `docs/STATUS.md` | 状态快照 |

## 七、已知问题

- **与 `TerrariaOverhaul` 的音乐模块冲突**：它的 `[Music] EnableMusicPlaybackPositionPreservation`
  会对 `Main.UpdateAudio()` 做 ILHook，使 tML 动态音轨进入"标志在播、样本流已断"的状态，
  表现为世界内模组音乐完全静音（菜单不受影响）。**与本模组无关**，影响所有模组音乐。
  解法：关闭该选项，或关闭 `TerrariaOverhaul`。
- **服务端不加载音频资源**：服务器上与本模组相关的音频逻辑全部跳过，属预期行为。
- **未验证**：真实声卡上的听感、设备热切换、最小化恢复、长时间（30 分钟以上）稳定性。

## 八、致谢与授权

- 原始模组与全部音频素材：**RighteousRyan** 及 `description.txt` 中列出的音效作者。
- 本 fork 的流式改造、主菜单音乐修复与诊断工具：本仓库维护者。
- 仓库未附 `LICENSE`，上游亦未声明授权条款。**二次分发或商用前请先联系原作者**。
