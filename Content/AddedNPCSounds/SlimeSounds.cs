using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Audio;
using Microsoft.Xna.Framework;
using TerrariaAmbience.Core;
using TerrariaAmbience.Helpers;
using TerrariaAmbience.Sounds.SoundFilters;

namespace TerrariaAmbience.Content.AddedNPCSounds;

public class SlimeSounds : GlobalNPC {
    public override bool InstancePerEntity => true;

    public Vector2 oldVelocity;

    public static HashSet<int> Slimes = [];

    public static void PopulateSlimes() {
        for (int i = 0; i < NPCID.Search.Count; i++) {
            var name = NPCID.Search.GetName(i);

            if (!name.Contains("slime", StringComparison.CurrentCultureIgnoreCase)) continue;

            Slimes.Add(i);
        }
    }
    public override void PostAI(NPC npc) {
        if (Main.dedServ) return;
        if (!ModContent.GetInstance<AudioConfig>().slimySounds) return;

        if (!Slimes.Contains(npc.type)) return;

        var vel = npc.velocity;

        SoundStyle soundStyle = default;

        bool soundStyleGiven = false;

        if (vel.Y == 0 && oldVelocity.Y != 0) {
            int oneOrTwo = Main.rand.Next(1, 3);
            soundStyle = new SoundStyle($"TerrariaAmbience/Sounds/Custom/npcs/slimeland{oneOrTwo}") {
                // velocity.Y 是带符号的，落地时通常为正、但被弹起时可能为负；
                // 用带符号值钳到 [0,1] 会让一部分落地完全静音。
                Volume = MathHelper.Clamp(Math.Abs(oldVelocity.Y) / 2.5f, 0f, 1f),
                Pitch = GeneralHelpers.NaturalPitchVariance
            };
            soundStyleGiven = true;
        }
        else if (vel.Y != 0 && oldVelocity.Y == 0f) {
            soundStyle = new SoundStyle($"TerrariaAmbience/Sounds/Custom/npcs/slimejump") {
                Volume = 0.5f,
                Pitch = GeneralHelpers.NaturalPitchVariance
            };
            soundStyleGiven = true;
        }

        oldVelocity = vel;

        if (!soundStyleGiven) return;

        var param = SoundFilterSystem.LatestParams;
        var sfx = GeneralHelpers.PlaySound(soundStyle, npc.position);

        sfx?.ApplyFiltersQuick(param);
    }
}
public class SplashingSounds : GlobalNPC {
    public override bool InstancePerEntity => true;
    bool _wet;
    public override void PostAI(NPC npc) {
        if (Main.dedServ) return;

        var cfg3 = ModContent.GetInstance<AudioConfig>();

        if (cfg3.newSplashSounds)
            HandleSplashing(npc);
    }

    public void HandleSplashing(NPC npc) {
        bool justWet = npc.wet && !_wet;
        bool justUnwet = !npc.wet && _wet;

        if (justWet || justUnwet) {
            // 带符号的 velocity.Y 会让「跃出水面」这一帧算出负音量。
            var vel = MathF.Abs(npc.velocity.Y);

            const float loud_thresh = 10f;

            var soundSplash = new SoundStyle($"TerrariaAmbience/Sounds/Custom/ambient/environment/liquid/entity_splash_{(vel >= loud_thresh ? "heavy" : "light")}");

            // 先判 0 再判区间：否则 vel == 0 会先被写成 0 再被覆盖，逻辑顺序是反的。
            if (vel == 0f)
                soundSplash.Volume = 0.1f;
            else if (vel < loud_thresh)
                soundSplash.Volume = vel / loud_thresh / 4f;

            SoundEngine.PlaySound(soundSplash, npc.position);
        }

        _wet = npc.wet;
    }
}