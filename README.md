# Terraria Ambience

简体中文 · [English](README.en.md)

给《泰拉瑞亚》补环境音的模组：森林虫鸣、洞穴轰鸣、雨雪风声、按材质区分的脚步声，以及房间混响、遮挡与水下闷化。

> **This is a maintained fork.** It fixes a batch of audio-lifetime, multiplayer and dedicated-server defects in
> upstream *Terraria Ambience* 0.3.7.1, and adds a Simplified Chinese config UI.
> Upstream thread: <https://forums.terraria.org/index.php?threads/terraria-ambience-mod.104161/>

## 这个仓库是什么

| 项 | 值 |
|---|---|
| 上游 | RighteousRyan 的 Terraria Ambience，版本 `0.3.7.1` |
| 本仓库 | `ora233-fantaea/TerrariaAmbience-fantasea`，分支 `1.4`（主线）与 `stream-1.4`（流式验证） |
| 版本号 | **未改动**，仍为 `0.3.7.1`。区分方式是加载来源：日志里的 `from Local` / `from Workshop` |
| 运行环境 | tModLoader 2026.9.x / .NET 8 / FNA（FAudio 后端） |
| 硬依赖 | `TerrariaAmbienceAPI` 1.3.6.8（工坊 `2826615136`，仓库 `lib/` 内 DLL 与之字节一致） |
| 体量 | 442 个 tracked 文件；音频 372 个（284 ogg + 88 wav），磁盘 65.1 MB，解码后约 447 MB |

代码层面的改动集中在一处目标：**让同一套音色在长时间游玩、联机、专用服务器上不再出问题**。素材本身未重制。

## 功能

**环境床**

- 森林（晨/昼/暮/夜四套曲目）、丛林、沙漠、海洋、雪原、腐化/猩红、洞穴层、地狱
- 风雨、雷声、暴风雪；水下与低处的闷化处理

**交互音**

- 按材质区分的脚步声：草地、泥土、树叶、石质、光面石、大理石、金属与金属板、宝石、沙与砾石、雪、薄冰/厚冰、玻璃、粘液、水、木材/钝木/木地板、湿地
- 盔甲与时装的脚步层；NPC 与史莱姆的脚步、入水
- 开合箱子、合成（含工作台/熔炉/炼金台等分类）、物品栏切换
- 篝火噼啪声、木结构吱呀声、地牢骨骼声

**空间音频**

- 房间混响：按 flood fill 统计的墙与方块数量推导房间规模、衰减与预延迟
- 声音遮挡：被墙阻隔的音源会被低通并降低音量
- 联机同步：其他玩家的合成音效、昼夜森林曲目

## 安装

1. 安装 **tModLoader 2026.9.x**（.NET 8 版本）。
2. 把 `TerrariaAmbience.tmod` 与 `TerrariaAmbienceAPI.tmod` 一起放进
   `Documents\My Games\Terraria\tModLoader\Mods\`。
3. 在模组列表里同时启用两者。**只装主模组会加载失败**——`build.txt` 里是 `modReferences` 硬依赖。

同名模组同时存在于工坊与本地时，tML 的规则是**本地优先**，日志会明确写出加载来源：

```
[tML]: Selected TerrariaAmbience 0.3.7.1 for tML 2026.9.3.1 from Local.
```

## 音频设备要求：不要超过 48000 Hz

这是引擎层的限制，不是本模组的限制。tModLoader 官方 FAQ 的原文：

> Try changing your audio output rate to be less than or equal to 48000 Hz.
> **Rates greater than 48000 Hz may cause crashes while certain mods are enabled.**

原因链条：游戏不自己挑选输出格式，而是拿 Windows 端点的当前格式去开 WASAPI 共享流；协商是「全有或全无」。
失败时 FAudio 走的是 C 断言而不是托管异常：

```c
hr = IAudioClient_Initialize(...);
FAudio_assert(!FAILED(hr) && "Failed to initialize audio client!");
```

`FAudio_assert` 失败即 `abort()` —— 进程直接消失，`client.log` 里没有任何记录，唯一痕迹是 `Natives.log` 的一行断言。

因此建议：**48000 Hz / 16 或 24 bit，关闭音频增强，不要用独占模式**。
44.1 kHz 同样可用；96 / 192 kHz 不会提升任何东西（素材以 44.1k/48k 为主，另有少量 32k 与 2 个 96k 文件），
只会把每个声部的混音缓冲放大 2–4 倍，并把端点协商推进到驱动最容易失败的区间。

模组会在主菜单给出提示，但**仅当检测到的设备采样率高于 48 kHz 时**才显示，并直接给出实测数值。

## 配置

5 个配置面板，共 42 个字段：

| 面板 | 内容 |
|---|---|
| 音效与滤波 | 混响（开关、湿度、射线模式、最大作用面积、刷新间隔、高级算法）、遮挡、水下闷化，以及篝火、合成、史莱姆、木结构、动物、箱子、入水、奔跑音 |
| 脚步声 | 主音量、NPC 脚步音量、湿地脚步、盔甲/时装层 |
| 环境音 | 总音量与过渡强度、各群系独立音量（森林、雪原、丛林、腐化/猩红、沙漠、海洋、洞穴、地狱、微风、水下）、BOSS 战音乐联动、调试叠层 |
| 界面 | 主菜单 UI、环境预报 UI |
| 物品栏音效 | 开关、音量倍率、动态物品栏音效 |

**中文界面**：仓库内含 `Localization/zh-Hans_Mods.TerrariaAmbience.hjson`。tML 按游戏语言选择本地化文件，
需要把游戏语言设为**简体中文**才会生效；英文环境继续使用 `en-US` 那份。

`混响湿度`（`reverbWetness`，0–1，默认 `0.3`）是本地新增项，用来控制每个音源送进混响的量：
觉得室内仍偏糊就调低，觉得太干就调高。这是实时生效的，不需要重新打包。

## 从源码构建

### 前置条件

- .NET 8 SDK
- 已安装 tModLoader 2026.9.x（需要其中的 `tMLMod.targets` 与运行库）
- `lib/TerrariaAmbienceAPI.dll`（仓库已带）

### 关键约束：必须放在 ModSources 下

`TerrariaAmbience.csproj` 第一行导入的是 `..\tModLoader.targets`：

```xml
<Import Project="..\tModLoader.targets" />
```

这意味着工程**必须**位于 `<tModLoader>\ModSources\<任意目录>\` 下，让 tModLoader 在该层生成 targets 文件。
直接在其他位置 `dotnet build` 会因找不到导入文件而失败。

若手工搭建隔离环境，可以自己写一个同名的垫片指向真实文件：

```xml
<Project>
  <Import Project="D:\SteamLibrary\steamapps\common\tModLoader\tMLMod.targets" />
</Project>
```

### 编译

```bash
dotnet build TerrariaAmbience.csproj -c Release -p:BuildMod=false
```

`-p:BuildMod=false` 只编译出 DLL，不打 `.tmod`。工程通过 `Krafs.Publicizer` 公开 FNA 的内部成员
（`handle`、`dspSettings` 等），因此 `SoundEffectInstance` 的原生句柄可直接访问；这是混响与低通实现的前提。

### 打包

```bash
dotnet tModLoader.dll -build "<abs>/ModSources/TerrariaAmbience" \
  -eac "<abs>/ModSources/TerrariaAmbience/bin/Release/net8.0/TerrariaAmbience.dll" \
  -unsafe true -savedirectory "<abs>/savedir"
```

产物默认落在 `savedir\tModLoader\Mods\`。注意 `-build` 的工作目录必须是 tModLoader 安装目录（或复制了
`tModLoader.dll` / `ReLogic.dll` 的隔离目录），否则运行库解析会失败。

### 冒烟测试

```bash
dotnet tModLoader.dll -server -nosteam -savedirectory "<abs>/smoke" -modpath "<abs>/smoke/Mods"
```

期望看到 `Adding Content: Terraria Ambience → Finalizing Content → Adding Recipes`，且模组自身零 `Error`/`Exception`。

## 当前状态

`docs/STATUS.md` 是入口文档，包含完整清单与证据行号。摘要：

**已修复（两批）**

| 组 | 代表项 |
|---|---|
| 崩溃 / 联机 | 联机包字段顺序写反（发送 `Int32→Vector2→String`，接收按 `Int32→String→Vector2` 读）；三处缺 `Main.dedServ` 守卫；`SoundInstance` 空引用；两个从未真正 `Send` 的包 |
| 泄漏与每 tick 开销 | 位置化音源每次播放覆盖旧实例导致原生 voice 泄漏；篝火检测每 tick 扫 100×100 格；找点、raycast、水深采样缺前置概率门；FAudio 描述符与混响 voice 未释放 |
| 逻辑与听感 | 雷声概率公式、合成节流、箱子双播与下标越界、像素/tile 混用、曲目随机开区间、低通恒为 1.0、屋檐粘滞、水晶球分类缺失、BOSS 战音乐音量不回原值 |
| 混响 | 房间表面数量被当作 0–1 的小数使用：2500 格会得到 7.5 s 衰减 / 156 ms 预延迟 / `RoomSize` 1250（FAudio 上限 100）。已按 `roomScale` 归一 |
| 主菜单音乐 | 退出世界后非 50 号音轨静音。成因是 `Reuse()` 不清空已排队缓冲，必须先 `Stop(Immediate)` 再起播 |

**已知未处理**

| 项 | 性质 |
|---|---|
| `entityFootstepsVolume` 的 `[DefaultValue(true)]` 标在 `float` 上，导致 NPC 脚步恒为 0 | 一行可改，但会改变默认听感，待定 |
| 沥青 / 日耀板 / 钛金砖同时属于两个石质脚步集合，每步播两层 | 集合去交集即可 |
| 石质脚步 8 个变体中 4 个自带 20–40 ms 的第二次撞击 | 素材问题，只能重制 |
| `TerrariaAmbienceAPI` 的 A1–A12（仓库内只有 DLL，无工程） | 主模组未引用流式 API，属潜伏缺陷 |
| 解码内存 447 MB，其中 43 条 biome 环境床占 369 MB | 已出方案未实施，见 `docs/streaming-plan-2026-11-19.md` |

## 文档

| 路径 | 内容 |
|---|---|
| `docs/STATUS.md` | **入口**：现状、两批改动、机制结论、验证矩阵、后续顺序 |
| `docs/progress.md` | 原始缺陷台账（B1–B31，逐条行号与修法） |
| `docs/qa-report-2026-11-18.md` | 静默 QA 问题清单（35 条，按严重度排序，含两处更正） |
| `docs/terraria-ambienceapi-analysis.md` | `TerrariaAmbienceAPI` 完整拆解（版本谱系、三类基元、耦合方式、A1–A12） |
| `docs/fix-report-2026-11-19.md` | 第一批修复的逐条说明；§8 是室内脚步叠加声的完整诊断链 |
| `docs/streaming-plan-2026-11-19.md` | 流式改造的分阶段方案与 FNA/NVorbis 事实核查 |
| `docs/streaming-verification-2026-11-21.md` | 流式可行性验证：真实进程解码、接缝量化、API 修正清单 |

## 致谢

- **RighteousRyan** —— 原作者，模组本体与全部音频设计
- **William** —— 模组图标
- **音频素材**：InspectorJ、morgantj、kinoton、globofonia、wjoojoo、JoelAudio、MegaShroom、straget
- 上游社区：<https://discord.gg/pT2BzSG>

本分支的改动、诊断与文档由 fork 维护者完成；原始设计功劳全部属于上游作者。

## 许可

仓库内**没有** `LICENSE` 文件，上游也未声明许可协议。代码与音频素材的再分发权利以上游作者为准：
在公开发布或再分发之前，请先与 RighteousRyan 确认。本仓库的改动不改变这一前提。
