using Terraria.ModLoader;
using Terraria;
using Terraria.ID;
using TerrariaAmbience.Helpers;
using System.Linq;
using System.Collections.Generic;
using TerrariaAmbience.Core;
using Terraria.Audio;
using Microsoft.Xna.Framework;
using TerrariaAmbience.Content.Players;
using TerrariaAmbience.Content.AmbientAndMore;
using TerrariaAmbience.Sounds.SoundFilters;

namespace TerrariaAmbience.Sounds.TileSounds;

public class TileSoundsPlayer : ModPlayer {
    public SoundStyle WoodCreak;

    public static readonly List<Point> _woodsBuffer = [];

    // heavy fps loss.... no more!
    public override void PostUpdate() {
        var config = ModContent.GetInstance<AudioConfig>();
        if (!config.woodCreaks) return;

        Point playerTilePos = new((int)Player.Center.X / 16, (int)Player.Center.Y / 16);

        var chanceSuccess = Main.rand.NextBool(150);

        // if success, just calculate all once and choose one.
        if (!chanceSuccess) return;

        _woodsBuffer.Clear();

        // was i on crack doing an 80x80 square before???
        static bool IsTileTypeWood(int type) => FootstepHandler.BluntWood.Contains(type) || FootstepHandler.FilledWood.Contains(type);

        // 直接双层循环，避免为 1600 个格子分配一个 List<Point>（原先每次触发都要建一个新列表）。
        const int half = 20;
        int minX = playerTilePos.X - half;
        int maxX = playerTilePos.X + half;
        int minY = playerTilePos.Y - half;
        int maxY = playerTilePos.Y + half;

        for (int x = minX; x < maxX; x++) {
            if (x < 0 || x >= Main.maxTilesX) continue;

            for (int y = minY; y < maxY; y++) {
                if (y < 0 || y >= Main.maxTilesY) continue;

                Tile acTile = Framing.GetTileSafely(x, y);
                Tile belowTile = Framing.GetTileSafely(x, y + 1);

                // is wood?
                if (!IsTileTypeWood(acTile.TileType)) continue;
                // unsupported?
                if (belowTile.HasTile) continue;
                // actuated?
                if (acTile.CollisionType() != 1) continue;

                _woodsBuffer.Add(new Point(x, y));
            }
        }

        if (_woodsBuffer.Count == 0) return;

        int randWood = Main.rand.Next(_woodsBuffer.Count);

        var tileCoordChosen = _woodsBuffer[randWood];

        var param = SoundFilterSystem.LatestParams;

        int rand = Main.rand.Next(1, 8);
        WoodCreak = new SoundStyle($"TerrariaAmbience/Sounds/Custom/ambient/blocks/wood_creak{rand}") {
            PitchVariance = 0.1f,
            Volume = 0.04f,
        };

        var worldPos = tileCoordChosen.ToVector2().ToWorldCoordinates();

        // Dust.NewDustPerfect(worldPos, DustID.TerraBlade, Vector2.Zero);

        var soundInstance = GeneralHelpers.PlaySound(WoodCreak, worldPos);
        soundInstance?.ApplyFiltersQuick(param);
    }
}