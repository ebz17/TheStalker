using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace TheStalker
{
    /// <summary>
    /// Driven off three times within a minute (your light, or you spotting him), he's had enough of the close game: he
    /// slips out of your sight 45-70 m away and shadows you from further off (30-42 m) for 15-25 s - still following,
    /// still laughing - before he can charge again. Stops the hide / get spotted / run / hide-round-the-corner loop.
    /// </summary>
    internal static partial class Stalker
    {
        private static readonly List<float> DrivenOff = new List<float>();
        private static bool _retreatPending, _enragePending;
        private static float _enragedUntil;

        /// <summary>Full power (Blackout Addons): the facility is lit - your flashlight means nothing to him, he charges if you
        /// spot him within 15 m, and driving him off three times makes him angry instead of careful.</summary>
        private static bool FullPower => BlackoutLink.Present && BlackoutLink.Power == 2;
        /// <summary>Enraged: a charge that ignores your light, your eyes and the lit rooms.</summary>
        private static bool Enraged => Time.time < _enragedUntil;
        /// <summary>Spot him (or shoot him) this close and he comes at you instead of running.</summary>
        private static float SpotChargeDistance => FullPower ? Mathf.Max(15f, CommitDistance()) : CommitDistance();
        private static float _shadowUntil = -1f;

        /// <summary>Shadowing you from afar right now.</summary>
        private static bool Shadowing => Time.time < _shadowUntil;

        /// <summary>SetState -> Flee: count the times you drove him off.</summary>
        private static void NoteDrivenOff(string why)
        {
            if (why != "your flashlight" && why != "you looked at him") return;
            DrivenOff.Add(Time.time);
            DrivenOff.RemoveAll(t => Time.time - t > 60f);
            if (DrivenOff.Count >= 3)
            {
                DrivenOff.Clear();
                if (FullPower) _enragePending = true;    // lit up and chased off once too often: he gets angry
                else _retreatPending = true;
            }
        }

        /// <summary>He got away: if he's had enough, slip far off and shadow you from there. True = handled.</summary>
        private static bool MaybeRetreat()
        {
            if (!_retreatPending) return false;
            float shadow = Rnd(15f, 25f);
            var spot = _seen ? null : FindSpot(45f, 70f, behind: false, needPath: false, tries: 30, awayFromPlayer: true);
            if (spot.HasValue)
            {
                try { Bot.Mover.Teleport(spot.Value); }
                catch (Exception) { spot = null; }
            }
            if (!spot.HasValue)
            {
                // you can see him, or nowhere to slip to: no shadowing from right next to you - try again when he next gets away
                Dbg($"wants to slip away but can't right now ({(_seen ? "you see him" : "no hidden spot")}) - he hides instead");
                return false;
            }
            _retreatPending = false;
            SetState(Mode.Stalk, "had enough - shadows you from afar");
            _shadowUntil = Time.time + shadow;
            _approachAfter = Mathf.Max(_approachAfter, _shadowUntil + Rnd(3f, 8f));
            Dbg($"driven off 3 times in a minute - slips {(spot.Value - _me.Position).magnitude:0} m away and shadows you from 30-40 m for {shadow:0} s");
            return true;
        }

        private static void ResetRetreat()
        {
            DrivenOff.Clear();
            _retreatPending = _enragePending = false;
            _enragedUntil = 0f;
            _shadowUntil = -1f;
        }
    }
}
