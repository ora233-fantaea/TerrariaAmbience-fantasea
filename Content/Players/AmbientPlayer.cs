using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaAmbience.Common.Enums;
using TerrariaAmbience.Common.Systems;
using TerrariaAmbience.Core;
using TerrariaAmbience.Helpers;
using TerrariaAmbience.Sounds.SoundFilters;

namespace TerrariaAmbience.Content.Players;

public class AmbientPlayer : ModPlayer {
    // This class serves the purpose of playing sounds and/or playing footsteps.
    // public int WallsAround => ReverbAudioSystem.WallsAround(Player.Center, new(4, 4), out var coords);
    float _multInternal;
    public float InRoomAmbientMultiplier => MathHelper.Clamp(_multInternal, 0, 1f);

    public static SoundEffectInstance soundSlippyRoughInst;
    public static SoundEffectInstance soundSlippySmoothInst;
    // 音源是静态共享的，初始化标志也必须是静态的：否则每进一次世界都会重建一遍并丢弃旧实例。
    static bool _areSoundsInitialized;
    static bool _iceSoundsFailed;

    /// <summary>释放静态滑行音源。世界卸载与模组卸载时都必须调用。</summary>
    public static void DisposeIceSounds() {
        try {
            soundSlippyRoughInst?.Stop();
            soundSlippySmoothInst?.Stop();
        }
        catch {
            // 设备已回收时 Stop 可能抛异常，忽略即可。
        }

        soundSlippyRoughInst?.Dispose();
        soundSlippySmoothInst?.Dispose();
        soundSlippyRoughInst = null;
        soundSlippySmoothInst = null;
        _areSoundsInitialized = false;
        _iceSoundsFailed = false;
    }

    internal int timerUntilValidChestStateChange;

    int chestStateNew;
    int chestStateOld; // Same as above.

    public static HashSet<int> NonArmoryArmors = [];

    // 篝火扫描结果缓存：100×100 的方框每 tick 重扫一次没有意义，
    // 玩家不跨 tile 时结果完全相同，跨 tile 或满 30 tick 才重算。
    static HashSet<Point> _campfireCache;
    static Point _campfireCacheTile;
    static int _campfireCacheAge;

    static HashSet<Point> GetCampfireCache(Vector2 center) {
        var tile = center.ToTileCoordinates();
        _campfireCacheAge++;

        if (_campfireCache is null || tile != _campfireCacheTile || _campfireCacheAge >= 30) {
            _campfireCache = [.. CampfireDetection.GetNearbyCampfires(center, 50)];
            _campfireCacheTile = tile;
            _campfireCacheAge = 0;
        }

        return _campfireCache;
    }

    public static void DetermineNonArmoryArmors() {
        for (int i = 0; i < ItemID.Search.Count; i++) {
            var item = ItemID.Search.GetName(i);

            if (!item.Contains("wood", StringComparison.InvariantCultureIgnoreCase)) continue;

            NonArmoryArmors.Add(i);
        }
    }

    /// <summary>
    /// For the use of rain. Not modifiable publically due to potential anomalies happening otherwise.
    /// <para></para>
    /// Checks above the player for about 8 tiles for a ceiling. Used for wet-sounding footsteps.
    /// </summary>
    public bool HasTilesAbove { get; private set; }

    bool _wet; // old state of player wet
    public override void OnEnterWorld() {
        TerrariaAmbience.DefaultAmbientHandler.HandleEnterWorld();

        Main.NewTextMultiline("If your game is experiencing severe framerate issues upon world startup (i.e: right now), do not worry, the" +
            "\ngame is caching things to make your game run much smoother during gameplay.", c: Color.Orange);

        if (Main.netMode == NetmodeID.SinglePlayer) return;

        SyncAmbienceSystem.AskForAmbiences();
    }
    public override void PreUpdate() {
        //if (Main.keyState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.NumPad5) && Main.oldKeyState.IsKeyUp(Microsoft.Xna.Framework.Input.Keys.NumPad5)) {
        //    NPC.NewNPC(null, (int) Player.position.X - 1000, (int) Player.position.Y, NPCID.DukeFishron);
        //}

        if (Main.soundVolume == 0) return;

        var genCfg = ModContent.GetInstance<FootstepsConfig>();
        var audioCfg = ModContent.GetInstance<AudioConfig>();

        if (audioCfg.newSplashSounds)
            ManagePlayerSplashes();

        if (genCfg.wetStepsEnabled)
            HandleWetSteps();
        else
            // this essentially just works as a way to not have wet sounds play. no need to do extra magic.
            HasTilesAbove = true;

        ManageChestSounds();

        // now configurable!
        if (!audioCfg.runSoundsEnabled)
            Player.runSoundDelay = 100;
    }
    void ManageChestSounds() {
        var audioCfg = ModContent.GetInstance<AudioConfig>();
        if (!audioCfg.chestSounds) return;

        chestStateNew = Player.chest;

        timerUntilValidChestStateChange++;
        if (timerUntilValidChestStateChange < 60) {
            chestStateOld = chestStateNew;
            return;
        }

        int oldState = chestStateOld;
        // 无论后续是否发声都要推进状态，否则猪猪银行/保险箱这类负索引会让状态永远卡住。
        chestStateOld = chestStateNew;

        if (chestStateNew == oldState)
            return;

        const float vol_scale_open = 0.15f;
        const float vol_scale_close = 0.3f;

        // 关闭容器、或从一个容器直接切到另一个容器时，旧容器的关闭音只放一次。
        if (TryGetChestCenter(oldState, out var closePos)) {
            var close = new SoundStyle("TerrariaAmbience/Sounds/Custom/ambient/player/chest_close").WithVolumeScale(vol_scale_close).WithPitchOffset(GeneralHelpers.NaturalPitchVarianceLesser);
            GeneralHelpers.PlaySound(close, closePos);
        }

        if (TryGetChestCenter(chestStateNew, out var openPos)) {
            var open = new SoundStyle("TerrariaAmbience/Sounds/Custom/ambient/player/chest_open").WithVolumeScale(vol_scale_open).WithPitchOffset(GeneralHelpers.NaturalPitchVarianceLesser);
            GeneralHelpers.PlaySound(open, openPos);
        }
    }

    /// <summary>把箱子索引安全地换算成世界坐标；索引越界或对应箱子为空时返回 false。</summary>
    static bool TryGetChestCenter(int index, out Vector2 center) {
        center = default;

        if (index < 0 || index >= Main.chest.Length)
            return false;

        var chest = Main.chest[index];
        if (chest is null)
            return false;

        // +1 是因为那是容器的中心格；目前没有尺寸不同的箱子。
        center = new Vector2(chest.x + 1, chest.y + 1) * 16;
        return true;
    }
    void HandleWetSteps() {
        // 每 tick 都要重新判定：原先只在找到实心块时置 true、从不置 false，
        // 导致玩家走过一次屋檐/洞穴下方后，雨天湿地脚步声永久失效。
        HasTilesAbove = false;

        // This is a pretty niche finding for tiles above said player
        // can obviously be optimized... fml

        if (Player.wet) return;

        int checkX = (int)Player.Top.X / 16;
        int checkStartY = (int)Player.Top.Y / 16;
        int checkEndY = (int)Player.Top.Y / 16 - 30;

        for (int j = checkStartY; j > checkEndY; j--) {
            // don't bother checking further
            if (!WorldGen.InWorld(checkX, j)) break;

            Tile tile = Main.tile[checkX, j];

            // check for solid tiles only
            if (!tile.HasTile || !Main.tileSolid[tile.TileType]) continue;

            HasTilesAbove = true;
            break;
        }
    }
    public override void PostUpdate() {
        HandleIceScraping();

        UpdateLocalPlayer();
    }
    public void UpdateLocalPlayer() {
        if (Player.whoAmI != Main.myPlayer)
            return;

        // 1f - the final value is the lowest value this can achieve, i.e: 0.8f maps to 0.2f lowest
        float desired = 1f - FloodFillSystem.PlayerRoom.PercentEnclosed * 0.85f;
        _multInternal = GeneralHelpers.RoughStep(_multInternal, desired, 0.02f);

        float crackleVolume, cracklePan = 0f;

        var audioCfg = ModContent.GetInstance<AudioConfig>();
        // this is my fix
        if (audioCfg.campfireSounds) {
            var campfires = GetCampfireCache(Player.Center);
            float closestDist = float.MaxValue;
            var closest = Vector2.Zero;

            foreach (var campfire in campfires) {
                var i = campfire.X;
                var j = campfire.Y;
                var campfireCenter = new Vector2(i, j) * 16 + new Vector2(24, 16);

                var dist = Vector2.Distance(Player.Center, campfireCenter);
                if (dist < closestDist) {
                    closest = campfireCenter;
                    closestDist = dist;
                }
            }

            // Dust.NewDustPerfect(closest, DustID.XenonMoss, Vector2.Zero);
            var campDist = Vector2.Distance(Player.Center, closest);
            var useLeftPan = Player.Center.X - closest.X < 0;

            float maxDist = 780f;
            float campfireVolumeScalar = 0.75f;

            // is pan not working due to it being stereo sound?
            cracklePan = MathHelper.Clamp(campDist / maxDist * (useLeftPan ? -1 : 1) / 2, -1, 1);
            crackleVolume = MathHelper.Clamp(1f - campDist / maxDist * campfireVolumeScalar, 0, 1);

            var crackle = TerrariaAmbience.DefaultAmbientHandler?.CampfireCrackleInstance;
            if (crackle is not null) {
                crackle.Volume = crackleVolume;
                // pan does not currently work since it's stereo instead of mono
                crackle.Pan = cracklePan;
            }
        }
    }
    /// <summary>
    /// Gets distance from surface of water to the player.
    /// </summary>
    /// <param name="heightCheck">The maximum tiles to check above the player.</param>
    /// <param name="checkPos">The position to check water pressure for. By default is the player's position.</param>
    /// <returns>The subpixel Y-component distance from the player's head to the surface.</returns>
    public float GetWaterPressureFloat(int heightCheck) => TileUtils.GetWaterPressureFloat(heightCheck, Player.position);
    /// <summary>
    /// Used for when the "splashes" config is enabled.
    /// </summary>
    public void ManagePlayerSplashes() {
        bool justWet = Player.wet && !_wet;
        bool justUnwet = !Player.wet && _wet;

        if (justWet || justUnwet) {
            var vel = Math.Abs(Player.velocity.Y);

            const float loud_thresh = 10f;

            var soundSplash = new SoundStyle($"TerrariaAmbience/Sounds/Custom/ambient/environment/liquid/entity_splash_{(vel >= loud_thresh ? "heavy" : "light")}") {
                //if (vel < loud_thresh)
                Volume = vel / loud_thresh / 4,
                Pitch = GeneralHelpers.NaturalPitchVariance
            };
            //else
            //soundSplash.Volume = vel / 4;
            if (vel == 0)
                soundSplash.Volume = 0.1f;

            GeneralHelpers.PlaySound(soundSplash, Player.position);
        }

        // for some reason heavy sounds arent playing

        _wet = Player.wet;
    }

    public float GetArmorStepVolume() {
        bool hasHeadArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.HeadSlot, out var head);
        bool hasChestArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.ChestSlot, out var chest);
        bool hasLegArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.LegSlot, out var legs);

        float determinedValue = 0f;

        // 10 <= x <= 12 == vanity armor
        bool isNotMetallic(Item item) {
            return NonArmoryArmors.Contains(item.type);
        }
        bool hasVanityCovering(int context) {
            return !Player.armor[context + 10].IsAir;
        }

        // what the sigma. (ryan, 2025)
        if (hasHeadArmor) {
            if (!head.vanity) {
                if (!isNotMetallic(head)) {
                    if (!hasVanityCovering(GeneralHelpers.IDs.ArmorSlotID.HeadSlot)) {
                        // 原先写成 *= 1.3f，但初值是 0，头盔加成永远为 0；这里按加法语义修正。
                        determinedValue += 0.3f;
                    }
                }
            }
        }
        if (hasChestArmor) {
            if (!chest.vanity) {
                if (!isNotMetallic(chest)) {
                    if (!hasVanityCovering(GeneralHelpers.IDs.ArmorSlotID.ChestSlot)) {
                        determinedValue += 0.05f;
                    }
                }
            }
        }
        if (hasLegArmor) {
            if (!legs.vanity) {
                if (!isNotMetallic(legs)) {
                    if (!hasVanityCovering(GeneralHelpers.IDs.ArmorSlotID.LegSlot)) {
                        determinedValue += 0.01f;
                    }
                }
            }
        }

        return determinedValue;
    }

    public float GetVanityStepVolume() {
        bool hasHeadArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.VanityHeadSlot, out var head);
        bool hasChestArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.VanityChestSlot, out var chest);
        bool hasLegArmor = GeneralHelpers.CheckPlayerArmorSlot(Player, GeneralHelpers.IDs.ArmorSlotID.VanityLegSlot, out var legs);

        float determinedValue = 0f;

        // 10 <= x <= 12 == vanity armor
        if (hasHeadArmor) {
            if (head.vanity) {
                // 同 GetArmorStepVolume：乘在 0 上恒为 0，改为加法。
                determinedValue += 0.35f;
            }
        }
        if (hasChestArmor) {
            if (chest.vanity) {
                determinedValue += 0.14f;
            }
        }
        if (hasLegArmor) {
            if (legs.vanity) {
                determinedValue += 0.1f;
            }
        }

        return determinedValue;
    }

    public void HandleContainerOpenings(ContainerContext context, out bool opened) {
        opened = false;
        chestStateNew = Player.chest;
        if (timerUntilValidChestStateChange > 10) {
            // Main.NewText($"{chestStateNew} : {chestStateOld}");
            switch (context) {
                case ContainerContext.Chest:
                    // > -1
                    if (chestStateNew >= -1) {
                        if ((chestStateNew != chestStateOld) && chestStateNew > -1)
                            opened = true;
                        // orignal closed, none other opened
                        if ((chestStateNew != chestStateOld) && chestStateNew == -1 && chestStateOld > -1)
                            opened = false;
                        // original closed, new opened
                        else if ((chestStateNew != chestStateOld) && chestStateOld > -1)
                            opened = false;
                    }
                    break;
                case ContainerContext.PiggyBank:
                    // -2
                    break;
                case ContainerContext.Safe:
                    // -3
                    break;
            }
        }
        chestStateOld = chestStateNew;
    }

    public void HandleIceScraping() {
        if (Main.dedServ) return;

        // 这两个音源是静态共享的，只有本地玩家的状态可以驱动它；
        // 否则多人游戏里会被其他玩家的 slippy 状态覆盖。
        if (Player.whoAmI != Main.myPlayer) return;

        if (!_areSoundsInitialized) {
            if (_iceSoundsFailed) return;

            try {
                soundSlippyRoughInst = Mod.Assets.Request<SoundEffect>("Sounds/Custom/ambient/player/ice_slide_rough", ReLogic.Content.AssetRequestMode.ImmediateLoad).Value.CreateInstance();
                soundSlippySmoothInst = Mod.Assets.Request<SoundEffect>("Sounds/Custom/ambient/player/ice_slide_smooth", ReLogic.Content.AssetRequestMode.ImmediateLoad).Value.CreateInstance();
            }
            catch (Exception e) {
                _iceSoundsFailed = true;
                DisposeIceSounds();
                _iceSoundsFailed = true;
                Mod.Logger.Warn($"TerrariaAmbience: failed to create ice slide sound instances: {e.Message}");
                return;
            }

            if (soundSlippyRoughInst is null || soundSlippySmoothInst is null) {
                _iceSoundsFailed = true;
                DisposeIceSounds();
                _iceSoundsFailed = true;
                Mod.Logger.Warn("TerrariaAmbience: ice slide sound instances unavailable; skipping ice scraping sounds.");
                return;
            }

            soundSlippyRoughInst.IsLooped = true;
            soundSlippyRoughInst.Play();
            soundSlippyRoughInst.Volume = 0f;
            soundSlippySmoothInst.IsLooped = true;
            soundSlippySmoothInst.Play();
            soundSlippySmoothInst.Volume = 0f;
            _areSoundsInitialized = true;
        }

        bool slippyRough = Player.slippy;
        bool slippySmooth = Player.slippy2;

        if (slippyRough) {
            soundSlippyRoughInst.Volume = Math.Abs(Player.velocity.X / 50f) * GeneralHelpers.GetVolumeFromPosition(Player.Bottom) * Main.soundVolume;
            soundSlippyRoughInst.Pan = GeneralHelpers.GetPanFromPosition(Player.Bottom);
        }
        else
            soundSlippyRoughInst.Volume -= 0.01f;
        if (slippySmooth) {
            soundSlippySmoothInst.Volume = Math.Abs(Player.velocity.X / 50f) * GeneralHelpers.GetVolumeFromPosition(Player.Bottom) * Main.soundVolume;
            soundSlippySmoothInst.Pan = GeneralHelpers.GetPanFromPosition(Player.Bottom);
        }
        else
            soundSlippySmoothInst.Volume -= 0.01f;
        soundSlippyRoughInst.Volume = MathHelper.Clamp(soundSlippyRoughInst.Volume, 0f, 0.5f);
        soundSlippySmoothInst.Volume = MathHelper.Clamp(soundSlippySmoothInst.Volume, 0f, 0.5f);

        soundSlippyRoughInst.ApplyReverb(SoundFilterSystem.LatestParams);
        soundSlippySmoothInst.ApplyReverb(SoundFilterSystem.LatestParams);
    }
}