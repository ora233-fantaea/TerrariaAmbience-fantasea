using Terraria.ModLoader;
using Terraria;
using Microsoft.Xna.Framework;
using Terraria.ID;
using TerrariaAmbience.Core;
using System.Linq;
using System;
using TerrariaAmbience.Content;
using TerrariaAmbience.Helpers;
using Terraria.Audio;
using TerrariaAmbience.Content.AmbientAndMore;
using TerrariaAmbience.Content.Players;
using Microsoft.Xna.Framework.Audio;
using System.IO;
using System.Collections.Generic;
using Terraria.ModLoader.Core;
using System.Reflection;
using TerrariaAmbience.Sounds;
using TerrariaAmbience.Sounds.SoundFilters;
using TerrariaAmbience.Sounds.SoundFilters.FAudioHacks;
using TerrariaAmbience.Common.Systems;
using TerrariaAmbience.Content.AddedNPCSounds;

namespace TerrariaAmbience;

public partial class TerrariaAmbience : Mod {
    // TODO: add all other types to the call

    public const float DEFAULT_FPS = 60f;
    public static float WorkaroundDeltaTime { get; private set; }

    public static bool WeaponOutLoaded;

    const string AmbienceSoundPrefix = "TerrariaAmbience/Sounds/";
    const int MaxForwardedSfxPerSecond = 30;

    static uint _sfxForwardWindow;
    static readonly int[] _sfxForwardCount = new int[256];

    // 开启 bossFightMusicAdjust 前的原始音乐音量；关闭该选项时还原，避免静默改掉用户设置。
    static float _musicVolumeBeforeAdjust = -1f;

    public override object Call(params object[] args) {
        try {
            string message = args[0] as string;
            if (message == "AddTilesToList") {
                Mod mod = args[1] as Mod;
                string listName = args[2] as string; // Can be Stone, Grass, Sand, Snow, or Dirt (FOR NOW, OR EVER) (UPDATE THIS)
                object boxedInstance = args[3];

                if (!Main.dedServ) {
                    if (mod != null && boxedInstance is string[] s) {
                        if (listName == "Grass")
                            TileDetection.AddTilesToList(mod, FootstepHandler.GrassBlocks, s);
                        else if (listName == "Stone")
                            TileDetection.AddTilesToList(mod, FootstepHandler.StoneBlocks, s);
                        else if (listName == "Sand")
                            TileDetection.AddTilesToList(mod, FootstepHandler.SandBlocks, s);
                        else if (listName == "Snow")
                            TileDetection.AddTilesToList(mod, FootstepHandler.SnowBlocks, s);
                        else if (listName == "Dirt")
                            TileDetection.AddTilesToList(mod, FootstepHandler.DirtBlocks, s);
                        else if (listName == "Metal")
                            TileDetection.AddTilesToList(mod, FootstepHandler.MetalBlocks, s);
                        else if (listName == "Ice")
                            TileDetection.AddTilesToList(mod, FootstepHandler.WeakIceBlocks, s);
                        else if (listName == "Leaf")
                            TileDetection.AddTilesToList(mod, FootstepHandler.LeafBlocks, s);
                        else if (listName == "Glass")
                            TileDetection.AddTilesToList(mod, FootstepHandler.GlassBlocks, s);
                        else if (listName == "GraniteMarble")
                            TileDetection.AddTilesToList(mod, FootstepHandler.Marbles, s);
                        else if (listName == "SmoothStone")
                            TileDetection.AddTilesToList(mod, FootstepHandler.SmoothStones, s);
                        else if (listName == "Sticky")
                            TileDetection.AddTilesToList(mod, FootstepHandler.StickyBlocks, s);
                        else if (listName == "Gem")
                            TileDetection.AddTilesToList(mod, FootstepHandler.GemBlocks, s);
                        else {
                            Logger.Info("Mod.Call failure: Unknown tile list specified.");
                            return "Unknown tile list specified.";
                        }
                        Logger.Info($"Successfully added modded tiles ({string.Join(", ", s)}) to the {listName} tile list.");
                    }
                    else if (mod == null && boxedInstance is int[] i) {
                        if (listName == "Grass")
                            TileDetection.AddTilesToList(FootstepHandler.GrassBlocks, i);
                        else if (listName == "Stone")
                            TileDetection.AddTilesToList(FootstepHandler.StoneBlocks, i);
                        else if (listName == "Sand")
                            TileDetection.AddTilesToList(FootstepHandler.SandBlocks, i);
                        else if (listName == "Snow")
                            TileDetection.AddTilesToList(FootstepHandler.SnowBlocks, i);
                        else if (listName == "Dirt")
                            TileDetection.AddTilesToList(FootstepHandler.DirtBlocks, i);
                        else if (listName == "Metal")
                            TileDetection.AddTilesToList(FootstepHandler.MetalBlocks, i);
                        else if (listName == "Ice")
                            TileDetection.AddTilesToList(FootstepHandler.WeakIceBlocks, i);
                        else if (listName == "Leaf")
                            TileDetection.AddTilesToList(FootstepHandler.LeafBlocks, i);
                        else if (listName == "Glass")
                            TileDetection.AddTilesToList(FootstepHandler.GlassBlocks, i);
                        else if (listName == "GraniteMarble")
                            TileDetection.AddTilesToList(FootstepHandler.Marbles, i);
                        else if (listName == "SmoothStone")
                            TileDetection.AddTilesToList(FootstepHandler.SmoothStones, i);
                        else if (listName == "Sticky")
                            TileDetection.AddTilesToList(FootstepHandler.StickyBlocks, i);
                        Logger.Info($"Successfully added modded tiles ({string.Join(", ", i.Select(id => TileID.Search.GetName(id)))} to the {listName} tile list.");
                    }
                }

                return "Tiles added successfully!";
            }
            else if (message == "AddTilesToCraftingStations") {
                Mod mod = args[1] as Mod;
                string listName = args[2] as string; // use all crafting station stuff
                object boxedInstance = args[3];
            }
            else
                Logger.Error("Call Error: Unknown Message: " + message);
        }
        catch (Exception e) {
            Logger.Error("Mod.Call Error: " + e.StackTrace + e.Message);
        }
        return "Call Failed";
    }

    string _versCache;

    public static AmbientHandler DefaultAmbientHandler;
    public static FootstepHandler DefaultFootstepHandler;

    internal static List<ModBiome> dbg_modBiomes;

    public override void Load() {
        _versCache = Main.versionNumber;
        Main.versionNumber += $", Terraria Ambience v{Version}";

        // 服务端不需要任何音频资源。InitializeAllAmbienceToAvoidRuntimeOverhead 用的是
        // DeferredLoad，请求会在随后的资源加载阶段真的把 24 条森林音轨解压进内存。
        if (!Main.dedServ) {
            AmbientHandler.InitializeAllAmbienceToAvoidRuntimeOverhead();
            AmbientPlayer.DetermineNonArmoryArmors();
            SlimeSounds.PopulateSlimes();
        }
        // calls DefaultAmbientHandler.Initialize() via the ctor
        // Ambience.Initialize();

        On_Main.Update += Main_Update;

        MethodDetours.DetourAll();
    }
    void Main_Update(On_Main.orig_Update orig, Main self, GameTime gameTime) {
        orig(self, gameTime);

        if (Main.dedServ) return;

        var audioCfg = ModContent.GetInstance<AudioConfig>();
        // why is this shit here
        if (audioCfg.campfireSounds && !Main.gameMenu) {
            if (!DefaultAmbientHandler?.CampfireCrackleInstance?.IsPlaying() == true)
                DefaultAmbientHandler.CampfireCrackleInstance.Play();
        }
        else {
            if (DefaultAmbientHandler?.CampfireCrackleInstance != null)
                DefaultAmbientHandler.CampfireCrackleInstance.Volume = 0f;
        }

        if (ModContent.GetInstance<AmbientConfig>().debugInterface && !Main.gameMenu) {
            MethodDetours.mouseWallOrTile = "None";

            var mTile = Main.tile[Main.MouseWorld.ToTileCoordinates()];

            if (mTile.HasTile) {
                MethodDetours.mouseWallOrTile = "Tile=" + TileID.Search.GetName(mTile.TileType);
            }
            else if (mTile.WallType != WallID.None) {
                MethodDetours.mouseWallOrTile = "Wall=" + WallID.Search.GetName(mTile.WallType);
            }
        }
        GeneralHelpers.ClickHandling();
        GeneralHelpers.UpdateButtons();

        DefaultAmbientHandler?.Update();
        DefaultFootstepHandler?.Update();

        GradientGlobals.Update();

        var ambCfg = ModContent.GetInstance<AmbientConfig>();
        if (ambCfg.bossFightMusicAdjust) {
            bool foundBoss = false;
            foreach (var npc in Main.ActiveNPCs) {
                if (!npc.boss) continue;

                foundBoss = true;
                break;
            }

            // 第一次启用时记录玩家原本的音乐音量，关闭功能时还原。
            // 原实现直接把 Main.musicVolume 收敛到固定值，退出后用户设置就被改掉了。
            if (_musicVolumeBeforeAdjust < 0f)
                _musicVolumeBeforeAdjust = Main.musicVolume;

            const float epsilon = 0.002f;
            var max = MathF.Round(ambCfg.bossFightMusicVolume, 2);
            var min = MathF.Round(ambCfg.bossFightMusicMin, 2);
            Main.musicVolume = MathHelper.Lerp(Main.musicVolume, foundBoss ? max : min, 0.02f * WorkaroundDeltaTime);

            if (Main.musicVolume - min < epsilon)
                Main.musicVolume = min;
            else if (max - Main.musicVolume < epsilon)
                Main.musicVolume = max;
        }
        else if (_musicVolumeBeforeAdjust >= 0f) {
            Main.musicVolume = _musicVolumeBeforeAdjust;
            _musicVolumeBeforeAdjust = -1f;
        }

        if (Main.gameMenu)
            return;

        WorkaroundDeltaTime = MathF.Round(60f * (float)gameTime.ElapsedGameTime.TotalSeconds * 1000) / 1000;
    }
    public override void PostSetupContent() {
        DefaultAmbientHandler = new();

        WeaponOutLoaded = ModLoader.HasMod("WeaponOutLite");

        dbg_modBiomes = [.. ModContent.GetContent<ModBiome>()];
    }

    // sadly we put this here since ModSystem.PostSetupContent runs after the mod's
    public override void PostAddRecipes() {
        if (Main.dedServ) return;

        MenuDetours.Init();
        SoundChanges.Init();

        DefaultFootstepHandler = new();

        NPCFootstepHandler.InitializeNPCStepping();

        var campfireCrackle = Assets.Request<SoundEffect>("Sounds/Custom/ambient/environment/campfire_crackle", ReLogic.Content.AssetRequestMode.ImmediateLoad).Value;
        DefaultAmbientHandler.CampfireCrackleInstance = campfireCrackle.CreateInstance(); // what's crashing?
        DefaultAmbientHandler.CampfireCrackleInstance.IsLooped = true;
    }

    public override void Unload() {
        UnloadAllSFXSoWeCanAvoidStupidCrashesThatHappenOnUnload();
        MenuDetours.AddMenuButtonsHook = null;
        Main.versionNumber = _versCache;
    }

    static void UnloadAllSFXSoWeCanAvoidStupidCrashesThatHappenOnUnload() {
        // 注意：这里不能因为 DefaultAmbientHandler 为 null 就整体返回，
        // 否则服务端（Handler 从未构造）会跳过下面所有音源的释放。
        var handler = DefaultAmbientHandler;
        if (handler is not null) {
            try {
                handler.CampfireCrackleInstance?.Stop();
            }
            catch {
                // 设备已回收时 Stop 可能抛异常，忽略。
            }

            handler.CampfireCrackleInstance?.Dispose();
            handler.CampfireCrackleInstance = null;
        }

        AmbientPlayer.DisposeIceSounds();
        Common.PositionalAudio2D.DisposeAll();
        FAudioReverbController.Shutdown();
    }

    public float lastPlayerPositionOnGround;
    public float delta_lastPos_playerBottom;

    public override void HandlePacket(BinaryReader reader, int whoAmI) {
        var packet = reader.ReadInt32();

        switch (packet) {
            case TAPID.SEND_CRAFT:
                if (Main.dedServ) {
                    // 顺序必须与接收端一致：客户端按 String → Vector2 读取。
                    // 原先是 Vector2 → String，客户端会把位置的前 4 字节当成字符串长度。
                    var path = reader.ReadString();
                    var pos = reader.ReadVector2();

                    var p = GetPacket();
                    p.Write(packet);
                    p.Write(path);
                    p.WriteVector2(pos);
                    p.Send(ignoreClient: whoAmI);
                }
                else {
                    var path = reader.ReadString();
                    var pos = reader.ReadVector2();
                    var audioCfg = ModContent.GetInstance<AudioConfig>();
                    var volScale = audioCfg.craftingSoundsVolume;
                    SoundEngine.PlaySound(new SoundStyle(path).WithVolumeScale(CraftSounds.UniversalSoundScale * volScale), pos);
                }
                break;
            case TAPID.SEND_AM_AMB:
                if (Main.dedServ) {
                    int morning = reader.ReadInt32();
                    int day = reader.ReadInt32();

                    var p = GetPacket();
                    p.Write(packet);
                    p.Write(morning);
                    p.Write(day);
                    // 原先写完就丢了，服务端从未真正转发，昼夜曲目在联机里根本不同步。
                    p.Send(ignoreClient: whoAmI);
                }
                else {
                    DefaultAmbientHandler.morningAmbienceForTheDay = reader.ReadInt32();
                    DefaultAmbientHandler.dayAmbienceForTheDay = reader.ReadInt32();

                    DefaultAmbientHandler.ForestMorning.ChangeTrack(AmbientHandler.AmbientPath + 
                        $"biome/forest/morning_{DefaultAmbientHandler.morningAmbienceForTheDay}");
                    DefaultAmbientHandler.ForestDay.ChangeTrack(AmbientHandler.AmbientPath + 
                        $"biome/forest/day_{DefaultAmbientHandler.dayAmbienceForTheDay}");

                    SyncAmbienceSystem.curMorningAmb = DefaultAmbientHandler.morningAmbienceForTheDay;
                    SyncAmbienceSystem.curDayAmb = DefaultAmbientHandler.dayAmbienceForTheDay;
                }
                break;
            case TAPID.SEND_PM_AMB:
                if (Main.dedServ) {
                    int evening = reader.ReadInt32();
                    int night = reader.ReadInt32();

                    var p = GetPacket();
                    p.Write(packet);
                    // peak readability
                    p.Write(evening);
                    p.Write(night);
                    // 同样补上缺失的发送。
                    p.Send(ignoreClient: whoAmI);
                }
                else {
                    DefaultAmbientHandler.eveningAmbienceForTheDay = reader.ReadInt32();
                    DefaultAmbientHandler.nightAmbienceForTheDay = reader.ReadInt32();

                    DefaultAmbientHandler.ForestEvening.ChangeTrack(AmbientHandler.AmbientPath + 
                        $"biome/forest/evening_{DefaultAmbientHandler.eveningAmbienceForTheDay}");
                    DefaultAmbientHandler.ForestNight.ChangeTrack(AmbientHandler.AmbientPath + 
                        $"biome/forest/night_{DefaultAmbientHandler.nightAmbienceForTheDay}");

                    SyncAmbienceSystem.curEveningAmb = DefaultAmbientHandler.eveningAmbienceForTheDay;
                    SyncAmbienceSystem.curNightAmb = DefaultAmbientHandler.nightAmbienceForTheDay;
                }
                break;
            case TAPID.SEND_AMB_SFX:
                if (Main.dedServ) {
                    var path = reader.ReadString();
                    var pos = reader.ReadVector2();
                    var pitch = reader.ReadSingle();

                    // 客户端可以伪造任意路径。不校验就等于允许任何客户端让服务器
                    // 向所有客户端广播任意资源路径，进而触发资源加载与线扫。
                    if (path is null || !path.StartsWith(AmbienceSoundPrefix, StringComparison.Ordinal)) {
                        Logger.Warn($"Ignored forwarded ambient SFX with unexpected path: {path}");
                        return;
                    }

                    // 简易限速：每客户端每秒最多转发 30 条。
                    uint window = Main.GameUpdateCount / 60;
                    if (window != _sfxForwardWindow) {
                        _sfxForwardWindow = window;
                        Array.Clear(_sfxForwardCount, 0, _sfxForwardCount.Length);
                    }
                    if (whoAmI >= 0 && whoAmI < _sfxForwardCount.Length && ++_sfxForwardCount[whoAmI] > MaxForwardedSfxPerSecond)
                        return;

                    var p = GetPacket();
                    p.Write(packet);
                    p.Write(path);
                    p.WriteVector2(pos);
                    p.Write(pitch);
                    p.Send(ignoreClient: whoAmI);
                }
                else {
                    var path = reader.ReadString();
                    var pos = reader.ReadVector2();
                    var pitch = reader.ReadSingle();

                    ImmersiveSoundsSystem.PlaySound(path, pos, pitch);
                }
                break;
            case TAPID.AMB_ASK:
                if (Main.dedServ) {
                    var mp = ModContent.GetInstance<TerrariaAmbience>().GetPacket();
                    mp.Write(TAPID.AMB_GET);
                    mp.Write(SyncAmbienceSystem.curMorningAmb);
                    mp.Write(SyncAmbienceSystem.curDayAmb);
                    mp.Write(SyncAmbienceSystem.curEveningAmb);
                    mp.Write(SyncAmbienceSystem.curNightAmb);

                    mp.Send(toClient: whoAmI);
                }
                // only handle on the server lol
                break;
            case TAPID.AMB_GET:
                if (Main.netMode == NetmodeID.MultiplayerClient) {
                    var m = reader.ReadInt32();
                    var d = reader.ReadInt32();
                    var e = reader.ReadInt32();
                    var n = reader.ReadInt32();

                    if (m > 0)
                    DefaultAmbientHandler.ForestMorning.ChangeTrack(AmbientHandler.AmbientPath +
                        $"biome/forest/morning_{m}");
                    if (d > 0)
                    DefaultAmbientHandler.ForestDay.ChangeTrack(AmbientHandler.AmbientPath +
                        $"biome/forest/day_{d}");
                    if (e > 0)
                    DefaultAmbientHandler.ForestEvening.ChangeTrack(AmbientHandler.AmbientPath +
                        $"biome/forest/evening_{e}");
                    if (n > 0)
                    DefaultAmbientHandler.ForestNight.ChangeTrack(AmbientHandler.AmbientPath +
                        $"biome/forest/night_{n}");

                    SyncAmbienceSystem.curMorningAmb = m;
                    SyncAmbienceSystem.curDayAmb = d;
                    SyncAmbienceSystem.curEveningAmb = e;
                    SyncAmbienceSystem.curNightAmb = n;

                    //Main.NewText($"Synced ambiences: {m}, {d}, {e}, {n}");
                }
                break;
            default:
                // 客户端与服务端的 mod 版本不一致时，未知包会持续到达；
                // 抛异常会让每次 HandlePacket 都失败，改成记录一次并忽略。
                Logger.Warn($"TerrariaAmbience: ignoring unidentified packet id {packet}.");
                return;
        }
    }
}