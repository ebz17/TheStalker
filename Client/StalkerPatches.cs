using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TheStalker
{
    /// <summary>Silent: no footsteps, no landing thuds, no voice. Fast: his sprint covers more ground.</summary>
    internal static class StalkerPatches
    {
        private static AccessTools.FieldRef<MovementContext, Player> _player;
        private static bool _logged;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound)),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(Mute)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlayGroundedSound)),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(Mute)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.Say)),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(Mute)));
            harmony.Patch(AccessTools.Method(typeof(BaseSpeaker), nameof(BaseSpeaker.Play)),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(MuteSpeaker)));
            _player = AccessTools.FieldRefAccess<MovementContext, Player>("_player");
            harmony.Patch(AccessTools.Method(typeof(MovementContext), nameof(MovementContext.ApplyMotion), new[] { typeof(Vector3), typeof(float) }),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(Faster)));
            // the game caps how fast a non-sprinting body may move (its speed limiter): lift it for his boosted steps
            harmony.Patch(AccessTools.Method(typeof(MovementContext), nameof(MovementContext.DirectApplyMotion), new[] { typeof(Vector3), typeof(float) }),
                prefix: new HarmonyMethod(typeof(StalkerPatches), nameof(LiftLimit)), postfix: new HarmonyMethod(typeof(StalkerPatches), nameof(RestoreLimit)));
        }

        private static bool Mute(Player __instance) => !Stalker.IsStalker(__instance);

        /// <summary>True only while The Stalker itself asks his voice for a line.</summary>
        public static bool AllowVoice;

        private static bool MuteSpeaker(BaseSpeaker __instance, EPhraseTrigger trigger, ref TagBank __result)
        {
            var body = Stalker.Body;
            if (body == null || !ReferenceEquals(body.Speaker, __instance)) return true;
            if (AllowVoice || trigger == EPhraseTrigger.OnDeath) return true;     // our chosen lines, and his death
            __result = null;
            return false;
        }

        /// <summary>His last ApplyMotion was boosted: the next DirectApplyMotion may go past the game's speed cap.</summary>
        private static bool _boostedStep;
        private static float _savedLimit;
        private static bool _lifted;

        private static void LiftLimit(MovementContext __instance)
        {
            _lifted = false;
            if (!_boostedStep || Stalker.Body == null) return;
            try
            {
                if (_player(__instance) != Stalker.Body) return;
                var cc = Stalker.Body.CharacterController;
                if (cc == null) return;
                _savedLimit = cc.SpeedLimit;
                cc.SpeedLimit = -1f;
                _lifted = true;
            }
            catch (Exception)
            {
                // leave the cap alone
            }
        }

        private static void RestoreLimit()
        {
            if (!_lifted) return;
            _lifted = false;
            _boostedStep = false;
            try
            {
                var cc = Stalker.Body?.CharacterController;
                if (cc != null && cc.SpeedLimit < 0f) cc.SpeedLimit = _savedLimit;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private static void Faster(MovementContext __instance, ref Vector3 motion, float deltaTime)
        {
            if (Stalker.Body == null) return;
            try
            {
                var body = _player(__instance);
                if (body != Stalker.Body) return;
                _boostedStep = false;
                if (Stalker.Charging) Stalker.ChargeSteps++;
                if (!Stalker.Sprinting || deltaTime <= 0f)
                {
                    if (Stalker.Charging) Stalker.ChargeNoRun++;
                    return;
                }
                Vector3 flat = new Vector3(motion.x, 0f, motion.z);
                float len = flat.magnitude;
                if (len < 0.0005f)
                {
                    if (Stalker.Charging) Stalker.ChargeStill++;
                    return;
                }
                if (Stalker.Charging) Stalker.ChargeBase += len / deltaTime;
                float stims = Stalker.StimBoost * Stalker.JunkieBoost;
                float want;
                if ((Stalker.Charging || Stalker.State == Mode.Flee) && !Stalker.Limping)
                {
                    // charging or running away: a set speed in m/s, whether the game lets him sprint or not
                    // (it holds bots to a walk near turns and doors - most of Labs)
                    want = Stalker.ChargeSpeed() * stims * deltaTime;
                    if (len / deltaTime < 0.15f)                  // he's stopping (at a door, at his spot): leave it
                    {
                        if (Stalker.Charging) Stalker.ChargeStill++;
                        return;
                    }
                }
                else
                {
                    want = len * Mathf.Clamp(Stalker.SpeedMult() * stims, 1f, 2.6f);
                }
                if (want <= len) return;
                // stay on the walkable floor: never step past the edge of the navmesh (corners, ledges)
                Vector3 from = body.Position;
                Vector3 dir = flat / len;
                if (UnityEngine.AI.NavMesh.Raycast(from, from + dir * (want * 2f), out var hit, UnityEngine.AI.NavMesh.AllAreas))
                {
                    want = Mathf.Max(len, Mathf.Min(want, hit.distance * 0.5f));
                    if (Stalker.Charging) Stalker.ChargeClipped++;
                }
                if (want <= len * 1.01f) return;
                float m = want / len;
                motion.x *= m;
                motion.z *= m;
                _boostedStep = true;
                if (Stalker.Charging) Stalker.ChargeBoosted++;
                if (!_logged)
                {
                    _logged = true;
                    Stalker.Log.LogInfo($"[Stalker] speed boost working (x{m:0.00}).");
                }
            }
            catch (Exception)
            {
                // leave the motion alone
            }
        }
    }
}
