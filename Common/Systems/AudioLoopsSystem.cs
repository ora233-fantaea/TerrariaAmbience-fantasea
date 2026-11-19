using TerrariaAmbience.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Terraria.ModLoader;
using System.IO;
using TerrariaAmbience.Helpers;
using Terraria;
using TerrariaAmbience.Core;
using TerrariaAmbience.Content.AmbientAndMore;
using TerrariaAmbience.Content.Players;

namespace TerrariaAmbience.Common.Systems;

public class AudioLoopsSystem : ModSystem
{
    public static PositionalAudio2D[] grassCritters = new PositionalAudio2D[10];
    public static PositionalAudio2D[] owls = new PositionalAudio2D[4];
    public override void PreSaveAndQuit() {
        // 专用服务器上 Initialize() 会提前返回，所有 ModAmbience 字段都是 null；
        // 这里不守卫就会在每次「保存退出」时抛 NRE。
        if (Main.dedServ) return;

        TerrariaAmbience.DefaultAmbientHandler?.RandomizeDawnTracks();
        TerrariaAmbience.DefaultAmbientHandler?.RandomizeDuskTracks();
    }

    public override void OnWorldUnload() {
        if (Main.dedServ) return;

        // 退出世界后音频设备可能被重建，旧的实例与原生 voice 必须释放。
        PositionalAudio2D.ReleaseAllInstances();
        AmbientPlayer.DisposeIceSounds();
    }

    public override void OnModLoad()
    {
        // 服务端不需要这些音源。原先 GetAssetValue 作为实参先于构造函数求值，
        // 导致专用服务器也把 14 个 wav 直接解压进内存（实测占用 25.9 MB）。
        if (Main.dedServ)
            return;
        grassCritters[0] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets1"), (5, 10), 0.25f, (0, 0)).WithName("QuietCricket");
        grassCritters[1] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets2"), (2, 3), 0.25f, (0, 0)).WithName("ClickCricket_0");
        grassCritters[2] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets3"), (3, 5), 0.25f, (0, 0)).WithName("ClickCricket_1");
        grassCritters[3] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets4"), (2, 5), 0.25f, (0, 0)).WithName("ClickCricket_2");
        grassCritters[4] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets5"), (1, 3), 0.25f, (0, 0)).WithName("HummingCricket");
        grassCritters[5] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets6"), (3, 6), 0.25f, (0, 0)).WithName("HummingCricket_DeepPitched");
        grassCritters[6] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets7"), (1, 2), 0.25f, (0, 0)).WithName("HummingCricket_Monotone");
        grassCritters[7] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets8"), (1, 2), 0.25f, (0, 0)).WithName("HummingCricket_Buzz");
        grassCritters[8] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/crickets9"), (1, 1), 0.25f, (0, 0)).WithName("YellingCricket");
        grassCritters[9] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/grasshopper"), (1, 1), 0.25f, (0, 0)).WithName("Grasshopper");
        owls[0] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/owl1"), (1, 2), 0.25f, (30, 50)).WithName("Owl_0");
        owls[1] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/owl2"), (1, 2), 0.25f, (35, 55)).WithName("Owl_1");
        owls[2] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/owl3"), (1, 2), 0.25f, (40, 60)).WithName("Owl_2");
        owls[3] = new PositionalAudio2D(GeneralHelpers.GetAssetValue<SoundEffect>(Mod, "Sounds/Custom/variousanimals/multipleowls"), (1, 1), 1f).WithName("CooCooOwl");
        owls[3].MaxVolume = 0.25f;
    }
    private static int _attempts; // make private lol
    private static string _reason;
    private static Vector2 RandomPosNearPlayer {
        get {
            var player = Main.LocalPlayer;
            Retry:
            _attempts++;
            var pos = player.Center + new Vector2(Main.rand.Next(-2000, 2000), Main.rand.Next(-500, 500));
            if (_attempts > 30)
            {
                _attempts = 0;
                return Vector2.Zero;
            }

            // Main.tile 越界会直接抛异常；世界边缘附近必须先判界。
            var tileX = (int)pos.X / 16;
            var tileY = (int)pos.Y / 16;
            if (!WorldGen.InWorld(tileX, tileY, 2) || !WorldGen.InWorld(tileX, tileY + 1, 2))
            {
                _reason = "OutOfWorld";
                goto Retry;
            }

            if (Collision.SolidCollision(pos, 1, 1))
            {
                _reason = "TileSolid";
                goto Retry;
            }

            if (!Collision.SolidCollision(pos + new Vector2(0, 16), 1, 1))
            {
                _reason = "TileUnderNotSolid";
                goto Retry;
            }

            var landingTile = Main.tile[(int)pos.X / 16, (int)pos.Y / 16 + 1];
            if (!FootstepHandler.GrassBlocks.Contains(landingTile.TileType) && !FootstepHandler.DirtBlocks.Contains(landingTile.TileType))
            {
                _reason = "IsNotGrassOrDirt";
                goto Retry;
            }
            _attempts = 0;
            return pos;
            // hopefully this is a fix, removing the wall check
        }
    }
    public override void PostUpdateEverything()
    {
        if (Main.dedServ) return;
        if (!ModContent.GetInstance<AudioConfig>().dynamicAnimalSounds) return;
        if (!Main.LocalPlayer.ZoneOverworldHeight) return;

        // 找点本身要跑若干次碰撞查询，必须先过概率门再求值；
        // 原先把属性写在 if 条件里，等于每 tick 无条件付一次代价。
        if (Main.rand.NextBool(680))
        {
            var pos = RandomPosNearPlayer;
            if (pos != Vector2.Zero)
                grassCritters[Main.rand.Next(grassCritters.Length)]?.Play(pos, 0.50f);
        }

        if (!Main.dayTime && Main.rand.NextBool(750))
        {
            var pos = RandomPosNearPlayer;
            if (pos != Vector2.Zero)
                owls[Main.rand.Next(owls.Length)]?.Play(pos, 0.75f);
        }
    }
}