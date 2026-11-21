using Terraria;
using Terraria.ModLoader;
using TerrariaAmbience.Common.Systems;
using TerrariaAmbience.Helpers;
using TerrariaAmbience.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using TerrariaAmbience.Content.Players;
using Microsoft.Xna.Framework.Audio;
using Terraria.ID;
using TerrariaAmbience.Sounds;
using TerrariaAmbience.Sounds.Streaming;
using System.Globalization;

namespace TerrariaAmbience.Content.AmbientAndMore;

public class AmbientHandler {
    public static string AmbientPath => $"Sounds/Custom/ambient/";
    public static string AmbientPathWithPrefix => $"{nameof(TerrariaAmbience)}/" + AmbientPath;

    public AmbientHandler() {
        Initialize();
    }

    public float TransitionHarshness;

    public StreamedAmbience BreezeLight;
    public StreamedAmbience BreezeHeavy;

    public StreamedAmbience ForestMorning;
    public StreamedAmbience ForestDay;
    public StreamedAmbience ForestEvening;
    public StreamedAmbience ForestNight;

    public StreamedAmbience Desert;

    public StreamedAmbience SnowDay;
    public StreamedAmbience SnowNight;
    // when it's snowy AND windy!
    public StreamedAmbience SnowAggro;

    public StreamedAmbience JungleDay;
    public StreamedAmbience JungleNight;
    public StreamedAmbience JungleUndergroundDay; // aka swamp
    public StreamedAmbience JungleUndergroundNight;

    public StreamedAmbience BeachCalm;
    // when it's watery AND windy!
    public StreamedAmbience BeachAggro;

    public StreamedAmbience EvilsCrimson;
    public StreamedAmbience EvilsCorruption;

    public StreamedAmbience CavernLayer;

    public StreamedAmbience Hell;

    public StreamedAmbience Underwater;
    public StreamedAmbience UnderwaterDeep;

    public StreamedAmbience RainLight;
    public StreamedAmbience RainMed;
    public StreamedAmbience RainHeavy;

    /// <summary>全部 25 条环境床。S4 之后只有流式一种实现。</summary>
    public List<StreamedAmbience> Ambiences { get; private set; } = [];

    /// <summary>
    /// 是否启用流式。关闭时不换实现，而是让每条音轨立刻走「常驻 SoundEffect」的兜底路径
    /// （与流解码失败时的降级是同一条代码），所以不存在第二套维护面。
    /// </summary>
    public static bool StreamingEnabled {
        get {
            try {
                return ModContent.GetInstance<AmbientConfig>().streamAmbientBeds;
            }
            catch {
                return true;
            }
        }
    }

    // 营火声不走环境床：它要求同 tick 出声，常驻 SoundEffect 更合适。
    // well it technically could...
    public SoundEffectInstance CampfireCrackleInstance;

    // these two fields will be chosen at either the last dusk or the last dawn respectively, aka pre-emptively chosen.
    public int morningAmbienceForTheDay;
    public int eveningAmbienceForTheDay;
    public int nightAmbienceForTheDay;
    public int dayAmbienceForTheDay;

    public const int NUM_EVENING_AMBIENCE = 6;
    public const int NUM_MORNING_AMBIENCE = 6;
    public const int NUM_NIGHT_AMBIENCE = 6;
    public const int NUM_DAY_AMBIENCE = 6;

    // values scraped from decompiled terraria. Main.cs::_minWind,_maxWind,_minRain,_maxRain
    public float MinWind = 0.34f;
    public float MaxWind = 0.4f;
    public float MinRain = 0.4f;
    public float MaxRain = 0.5f;

    public void Initialize() {
        if (Main.dedServ) return;

        var mod = ModContent.GetInstance<TerrariaAmbience>();

        BreezeLight = new(mod, AmbientPath + "environment/breeze_light", "BreezeLight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            var player = Main.LocalPlayer;
            return IsStrictlySurface;
        });
        BreezeHeavy = new(mod, AmbientPath + "environment/breeze_heavy", "BreezeHeavy", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            var player = Main.LocalPlayer;
            return IsStrictlySurface;
        });

            ForestMorning = new(mod, AmbientPath + $"biome/forest/morning_{Main.rand.Next(1, NUM_MORNING_AMBIENCE + 1)}", "ForestMorning", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
                return InAForest;
            });
            ForestDay = new(mod, AmbientPath + $"biome/forest/day_{Main.rand.Next(1, NUM_DAY_AMBIENCE + 1)}", "ForestDay", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
                return InAForest && Main.dayTime;
            });
            ForestEvening = new(mod, AmbientPath + $"biome/forest/evening_{Main.rand.Next(1, NUM_EVENING_AMBIENCE + 1)}", "ForestEvening", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
                return InAForest;
            });
            ForestNight = new(mod, AmbientPath + $"biome/forest/night_{Main.rand.Next(1, NUM_NIGHT_AMBIENCE + 1)}", "ForestNight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
                return InAForest && !Main.dayTime;
            });

        CavernLayer = new(mod, AmbientPath + "biome/cavern/wind", "CavernLayer", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            var player = Main.LocalPlayer;
            return player.Center.Y >= Main.worldSurface * 16;
        });

        Desert = new(mod, AmbientPath + "biome/desert/critters", "Desert", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InADesert;
        });

        SnowDay = new(mod, AmbientPath + "biome/snow/day", "SnowDay", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InATundra && Main.dayTime;
        });
        SnowNight = new(mod, AmbientPath + "biome/snow/night", "SnowNight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InATundra && !Main.dayTime;
        });
            SnowAggro = new(mod, AmbientPath + "biome/snow/windy", "SnowAggro", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
                return InATundra && Main.raining;
            });
        EvilsCrimson = new(mod, AmbientPath + "biome/crimson/rumbles", "EvilsCrimson", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InACrimson;
        });
        EvilsCorruption = new(mod, AmbientPath + "biome/corruption/roars", "EvilsCorruption", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InACorruption;
        });

        JungleDay = new(mod, AmbientPath + "biome/jungle/day", "JungleDay", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InAJungle;
        });
        JungleNight = new(mod, AmbientPath + "biome/jungle/night", "JungleNight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InAJungle;
        });
        // don't wokr... somehow?
        JungleUndergroundDay = new(mod, AmbientPath + "biome/jungle/underground_day", "JungleUndergroundDay", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InAJungle;
        });
        JungleUndergroundNight = new(mod, AmbientPath + "biome/jungle/underground_night", "JungleUndergroundNight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InAJungle;
        });

        BeachCalm = new(mod, AmbientPath + "biome/beach/waves_calm", "BeachCalm", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InABeach;
        });
        BeachAggro = new(mod, AmbientPath + "biome/beach/waves_aggro", "BeachAggro", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return InABeach;
        });
        Hell = new(mod, AmbientPath + "biome/hell/rumbles", "Hell", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            // save some resources by only controlling this if they're in the underworld
            // Main.rockLayer 的单位是 tile，player.position.Y 是像素，原先直接比较等于恒为真。
            return Main.LocalPlayer.position.Y > Main.rockLayer * 16;
        });
        Underwater = new(mod, AmbientPath + "environment/underwater_loop", "Underwater", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            var player = Main.LocalPlayer;

            return player.IsWaterSuffocating();
        });
        UnderwaterDeep = new(mod, AmbientPath + "environment/underwater_loop-deep", "UnderwaterDeep", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            var player = Main.LocalPlayer;

            return player.IsWaterSuffocating();
        });
        RainLight = new(mod, AmbientPath + "rain/light", "RainLight", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return !InATundra;
        });
        RainMed = new(mod, AmbientPath + "rain/med", "RainMedium", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return !InATundra;
        });
        RainHeavy = new(mod, AmbientPath + "rain/heavy", "RainHeavy", maxVolume: 1f, volumeStep: TransitionHarshness, (a) => {
            return !InATundra;
        });

        // 关闭流式不换实现，只把每条音轨钉死在兜底的常驻路径上（ImmediateLoad）。
        // 那条路必须预载，否则黎明/黄昏换曲那一帧会同步解压 8 MB 级的 ogg；
        // 开启流式则跳过，24 首森林曲目一共省下约 198 MB。
        StreamedAmbience.ForceResident = !StreamingEnabled;

        if (!StreamingEnabled)
            InitializeAllAmbienceToAvoidRuntimeOverhead();

        // finally, we add all of them via reflection because manual labor is stupid.
        Ambiences = [];
        foreach (var fld in GetType().GetFields().Where(x => x.FieldType.TypeHandle.Equals(typeof(StreamedAmbience).TypeHandle))) {
            if (fld.GetValue(this) is StreamedAmbience amb)
                Ambiences.Add(amb);
        }

        mod.Logger.Info($"Ambience beds: {Ambiences.Count} tracks, " +
                        $"streaming={(StreamedAmbience.ForceResident ? "disabled (resident fallback)" : "enabled")}");
    }

    public static void AddModBiomeTo(Mod mod, List<ModBiome> registry, params string[] biomeInternalNames) {
        foreach (string tile in biomeInternalNames) {
            if (mod.TryFind<ModBiome>(tile, out var found))
                registry.Add(found);
            else
                ModContent.GetInstance<TerrariaAmbience>().Logger.Error($"Mod Tile '{tile}' not found. Skipping over...");
        }
    }

    public static bool InAForest;
    public List<ModBiome> ModdedForests = [];
    public static bool InADesert;
    public List<ModBiome> ModdedDeserts = [];
    public static bool InATundra;
    public List<ModBiome> ModdedTundras = [];
    public static bool InABeach;
    public List<ModBiome> ModdedBeaches = [];
    public static bool InAJungle;
    public List<ModBiome> ModdedJungles = [];
    public static bool InACrimson;
    public List<ModBiome> ModdedEvilCrimsons = [];
    public static bool InACorruption;
    public List<ModBiome> ModdedEvilCorruptions = [];

    public static bool IsStrictlySurface;

    // FROM HERE: I am creating fields that are used to determine ambient noise changes or differences.
    public float WindThreshold = 20f;
    public bool IsWindTooHarsh;

    public void HandleEnterWorld() {
        if (Main.raining) {
            dayAmbienceForTheDay = AmbienceID.Day_Quiet;
            ForestDay.ChangeTrack(AmbientPath + $"biome/forest/day_{dayAmbienceForTheDay}");
        }
    }
    public static void InitializeAllAmbienceToAvoidRuntimeOverhead() {
        // different for-loops because these values could change.
        // also magic numbers because it's weird to look at an identifier as a loop limit.
        var mod = ModContent.GetInstance<TerrariaAmbience>();
        for (int i = 1; i <= NUM_MORNING_AMBIENCE; i++) {
            mod.Assets.Request<SoundEffect>(AmbientPath + $"biome/forest/morning_{i}");
        }
        for (int i = 1; i <= NUM_DAY_AMBIENCE; i++) {
            mod.Assets.Request<SoundEffect>(AmbientPath + $"biome/forest/day_{i}");
        }
        for (int i = 1; i <= NUM_EVENING_AMBIENCE; i++) {
            mod.Assets.Request<SoundEffect>(AmbientPath + $"biome/forest/evening_{i}");
        }
        for (int i = 1; i <= NUM_NIGHT_AMBIENCE; i++) {
            mod.Assets.Request<SoundEffect>(AmbientPath + $"biome/forest/night_{i}");
        }
    }
    public void RandomizeDuskTracks() {
        eveningAmbienceForTheDay = Main.rand.Next(1, NUM_EVENING_AMBIENCE + 1);
        nightAmbienceForTheDay = Main.rand.Next(1, NUM_NIGHT_AMBIENCE + 1);

        SyncAmbienceSystem.curEveningAmb = eveningAmbienceForTheDay;
        SyncAmbienceSystem.curNightAmb = nightAmbienceForTheDay;

        ForestEvening.ChangeTrack(AmbientPath + $"biome/forest/evening_{eveningAmbienceForTheDay}");
        ForestNight.ChangeTrack(AmbientPath + $"biome/forest/night_{nightAmbienceForTheDay}");
    }
    public void RandomizeDawnTracks() {
        morningAmbienceForTheDay = Main.rand.Next(1, NUM_MORNING_AMBIENCE + 1);
        dayAmbienceForTheDay = Main.rand.Next(1, NUM_DAY_AMBIENCE + 1);
        if (Main.raining)
            dayAmbienceForTheDay = AmbienceID.Day_Quiet;

        SyncAmbienceSystem.curMorningAmb = morningAmbienceForTheDay;
        SyncAmbienceSystem.curDayAmb = dayAmbienceForTheDay;

        ForestDay.ChangeTrack(AmbientPath + $"biome/forest/day_{dayAmbienceForTheDay}");
        ForestMorning.ChangeTrack(AmbientPath + $"biome/forest/morning_{morningAmbienceForTheDay}");
    }
    public void Update() {
        if (Main.dedServ)
            return;

        var player = Main.LocalPlayer;
        var aCfg = ModContent.GetInstance<AmbientConfig>();

        bool isHarshOk = float.TryParse(aCfg.transitionHarshness, 
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out var harshness);

        foreach (var amb in Ambiences) {
            if (Main.gameMenu) {
                amb.PlayInMenu = true;
                amb.MaxVolume = 0f; // no ambience in the main menu.
            }

            amb.Pitch = 0f;

            if (!isHarshOk) continue;
            TransitionHarshness = harshness;
            amb.VolumeStep = harshness;
        }


        if (Main.gameMenu) {
            // 主菜单里 MaxVolume 已被压到 0，这里继续推进包络让它们淡出并停机，
            // 否则退出世界后流会以最后的音量一直响下去。
            foreach (var amb in Ambiences)
                amb.Update();
            return;
        }

        InAForest = player.ZonePurity || player.ZoneMeteor || ModdedForests.Any(player.InModBiome) || (player.ZoneHallow && !player.ZoneDesert);
        InADesert = player.ZoneDesert || ModdedDeserts.Any(player.InModBiome);
        InATundra = (player.ZoneSnow && !player.ZoneUnderworldHeight) || ModdedTundras.Any(player.InModBiome);
        InACrimson = player.ZoneCrimson || ModdedEvilCrimsons.Any(player.InModBiome);
        InACorruption = player.ZoneCorrupt || ModdedEvilCorruptions.Any(player.InModBiome);
        InAJungle = player.ZoneJungle || ModdedJungles.Any(player.InModBiome);
        InABeach = (player.ZoneBeach && !player.ZoneRockLayerHeight) || ModdedBeaches.Any(player.InModBiome);

        IsStrictlySurface = player.ZoneOverworldHeight && !player.ZoneSkyHeight;

        IsWindTooHarsh = Math.Abs(Main.windSpeedCurrent) >= MaxWind;

        if (!isHarshOk)
            ModContent.GetInstance<AmbientConfig>().transitionHarshness = "0.01"; // a failsafe in case the player is stupid

        ForestMorning.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.FromMorning * ModContent.GetInstance<AmbientConfig>().forestVolumes[0];
        ForestDay.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.FromNoon * ModContent.GetInstance<AmbientConfig>().forestVolumes[1];
        ForestEvening.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.FromEvening * ModContent.GetInstance<AmbientConfig>().forestVolumes[2];
        ForestNight.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.FromMidnight * ModContent.GetInstance<AmbientConfig>().forestVolumes[3];

        // added AllDayPartNight and AllNightPartDay to JungleDay and JungleNight at 11/22/2023 @ 9:33 AM
        JungleDay.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.AllDayPartNight * ModContent.GetInstance<AmbientConfig>().jungleVolumes[0] * 0.75f; // it's a bit loud.
        JungleNight.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.AllNightPartDay * ModContent.GetInstance<AmbientConfig>().jungleVolumes[1] * 0.4f; // it's very loud by default.
        
        JungleUndergroundDay.MaxVolume = GradientGlobals.Underground * GradientGlobals.AllDayPartNight * ModContent.GetInstance<AmbientConfig>().jungleVolumes[2];
        JungleUndergroundNight.MaxVolume = GradientGlobals.Underground * GradientGlobals.AllNightPartDay * ModContent.GetInstance<AmbientConfig>().jungleVolumes[3] * 0.6f; // it's a bit louder than the day variant...

        SnowDay.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.WindSpeedsLow * ModContent.GetInstance<AmbientConfig>().snowVolume;
        SnowNight.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.WindSpeedsLow * ModContent.GetInstance<AmbientConfig>().snowVolume;
        var snowAggroMax = GradientGlobals.SkyToUnderground * GradientGlobals.RainIntensityForSnow * GradientGlobals.WindSpeedsHigh * ModContent.GetInstance<AmbientConfig>().snowVolume;
        SnowAggro.MaxVolume = snowAggroMax;
        // make quieter / bandpass filter when inside
        Desert.MaxVolume = GradientGlobals.SkyToUnderground * ModContent.GetInstance<AmbientConfig>().desertVolume;

        Desert.Pitch =
            (Main.dayTime ? 0f : -0.1f) -
            (InACrimson || InACorruption ? 0.5f : 0f);
        if (!Underwater.AreConditionsMet) Underwater.VolumeStep = TransitionHarshness * 2.5f;
        else Underwater.VolumeStep = TransitionHarshness;

        EvilsCorruption.MaxVolume = 1f;
        EvilsCrimson.MaxVolume = 1f;

        Underwater.MaxVolume = 0f;
        UnderwaterDeep.MaxVolume = 0f;

        // rain volume slider.
        RainLight.MaxVolume = GradientGlobals.RainIntensityForRainLight * GradientGlobals.SkyToUnderground;
        RainMed.MaxVolume = GradientGlobals.RainIntensityForRainMed * GradientGlobals.SkyToUnderground;
        RainHeavy.MaxVolume = GradientGlobals.RainIntensityForRainHeavy * GradientGlobals.SkyToUnderground * 0.6f;

        if (Underwater.AreConditionsMet) {
            // do volume lerp between underwater_loop and underwater_loop-deep
            // check X tiles from the player's head.
            var tileSize = 16;
            var tilesToCheck = 30;
            var waterDepth = player.GetModPlayer<AmbientPlayer>().GetWaterPressureFloat(tilesToCheck * tileSize);

            float lightMaxThresh = 20;
            float deepMinThresh = 15;
            float deepMaxThresh = 30;

            float lightMinThresh = 2;

            // we start the minValue in the negatives because we want some sort of audio if the player is in very shallow water.
            var underwaterLightGradient = Gradient.CreateFloat(
                waterDepth, -tileSize * lightMinThresh, tileSize * lightMaxThresh);
            var underwaterDeepGradient = Utils.GetLerpValue(tileSize * deepMinThresh, tileSize * deepMaxThresh, waterDepth);

            Underwater.MaxVolume = underwaterLightGradient;
            UnderwaterDeep.MaxVolume = underwaterDeepGradient;
        }

        BeachCalm.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.WindSpeedsLow * ModContent.GetInstance<AmbientConfig>().oceanVolume;
        BeachAggro.MaxVolume = GradientGlobals.SkyToUnderground * GradientGlobals.WindSpeedsHigh * ModContent.GetInstance<AmbientConfig>().oceanVolume;
        
        BreezeLight.MaxVolume = GradientGlobals.WindLight * GradientGlobals.SkyToUnderground * ModContent.GetInstance<AmbientConfig>().breezeVolume;
        BreezeHeavy.MaxVolume = GradientGlobals.WindCapped * GradientGlobals.SkyToUnderground * ModContent.GetInstance<AmbientConfig>().breezeVolume * 0.5f;
        CavernLayer.MaxVolume = GradientGlobals.Underground * ModContent.GetInstance<AmbientConfig>().cavernsVolume * 0.4f;
        
        Hell.MaxVolume = GradientGlobals.Hell * ModContent.GetInstance<AmbientConfig>().hellVolume;

        // maybe
        // float uncoveredPercentage = RoomDetectionPlayer.PlayerRoom.til

        // idk how to avoid looping again
        var pl = Main.LocalPlayer.GetModPlayer<AmbientPlayer>();
        var overallVolume = ModContent.GetInstance<AmbientConfig>().overallVolume;
        bool isUnderwater = Underwater.AreConditionsMet;

        for (int i = 0; i < Ambiences.Count; i++) {
            var amb = Ambiences[i];
            amb.MaxVolume *= pl.InRoomAmbientMultiplier * overallVolume;

            if (!isUnderwater) continue;

            // 原写法是 Pitch -= -0.05f，等价于 +0.05（水下反而升调），
            // 而且它依赖上一轮循环把 Pitch 归零才不至于逐帧累加。
            amb.Pitch = -0.05f;

            amb.MaxVolume *= 0.1f;
        }

        // 推进状态机：打开 → 预填 → 起播 → 补块 → 停机。
        for (int i = 0; i < Ambiences.Count; i++)
            Ambiences[i].Update();
    }
}
public static class AmbienceID {
    public const int Day_BirdsAndCrowsLoud = 1;
    public const int Day_BirdsSinging = 2;
    public const int Day_BirdsSingingLoud = 3;
    public const int Day_BirdsSingingQuiet = 4;
    public const int Day_Quiet = 5;
    public const int Day_BirdsSingingOften = 6;

    public const int Morning_CricketsQuiet = 1;
    public const int Morning_CricketsTrillingQuiet = 2;
    public const int Morning_CricketsAndCicadas = 3;
    public const int Morning_CricketsTrillingLoud = 4;
    public const int Morning_CicadasAndBugs = 5;
    public const int Morning_EverythingTrilling = 6;

    public const int Night_CricketsQuiet = 1;
    public const int Night_CricketsPersistent = 2;
    public const int Night_CicadasAndCrickets = 3;
    public const int Night_CicadasConstant = 4;
    // for some reason this was 4 for quite a while...
    public const int Night_LoudEverything = 5;
    public const int Night_TrillingCricketsAndFrogs = 6;

    public const int Evening_SinewaveCicadas = 1;
    public const int Evening_LoudCricketsWithCicadas = 2;
    public const int Evening_HumidSoundingCrickets = 3;
    public const int Evening_CricketsTrilling = 4;
    public const int Evening_CricketsAndQuietCicadas = 5;
    public const int Evening_VariousAnimals = 6;
}