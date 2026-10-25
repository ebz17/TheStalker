using System;
using EFT;
using EFT.HealthSystem;
using UnityEngine;

namespace TheStalker
{
    /// <summary>
    /// What makes him deadly: the backstab (a charge you never saw coming hits twice as hard, in the chest),
    /// more cuts while you're whited out, and the smell of blood (bleeding or badly hurt, he comes back much sooner).
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<bool> Backstab = () => true;
        public static Func<int> CutsPerAttack = () => 2;
        public static Func<bool> SmellsBlood = () => true;

        private static bool _unseenCharge;          // this charge: you haven't seen him or had your light on him
        private static int _cuts;                    // cuts in this attack
        private static float _firstCutAt = -100f, _nextBloodLog;

        /// <summary>Called when a charge starts (SetState -> Approach).</summary>
        private static void NewCharge(bool unseen)
        {
            _unseenCharge = unseen;
            _cuts = 0;
        }

        /// <summary>Every perception tick: once you see him (or light him up) in a charge, there's no backstab.</summary>
        private static void WatchCharge()
        {
            if ((State == Mode.Approach || State == Mode.Attack) && (_seen || _litByPlayer)) _unseenCharge = false;
        }

        /// <summary>You're bleeding or below half health.</summary>
        private static bool PlayerBleeding(out string why)
        {
            why = null;
            try
            {
                var hc = _me?.HealthController;
                if (hc == null) return false;
                // every body part (the whole-body list doesn't always carry a limb's bleeding)
                foreach (EBodyPart p in Enum.GetValues(typeof(EBodyPart)))
                {
                    foreach (var e in hc.GetAllActiveEffects(p))
                    {
                        if (e is IHeavyBleeding) { why = "heavy bleeding"; return true; }
                        if (e is ILightBleeding) why = "bleeding";
                    }
                }
                if (why != null) return true;
                float cur = 0f, max = 0f;
                foreach (EBodyPart p in Enum.GetValues(typeof(EBodyPart)))
                {
                    if (p == EBodyPart.Common) continue;
                    var h = hc.GetBodyPartHealth(p);
                    cur += h.Current;
                    max += h.Maximum;
                }
                if (max > 0f && cur < max * 0.5f) { why = "badly hurt"; return true; }
            }
            catch (Exception)
            {
                // no health info: no blood
            }
            return false;
        }

        /// <summary>How long he waits before his next charge after a hit and run.</summary>
        private static float NextChargeDelay()
        {
            if (SmellsBlood() && PlayerBleeding(out var why))
            {
                Dbg($"smells your blood ({why}) - he'll be back soon");
                return Rnd(10f, 20f);
            }
            if (SmellsBlood()) Dbg("no blood to smell (you're not bleeding and above half health) - he takes his time");
            return Rnd(30f, 60f);
        }

        /// <summary>While stalking: bleeding brings his next charge forward.</summary>
        private static void BloodHurries()
        {
            if (!SmellsBlood() || _approachAfter - Time.time <= 20f || !PlayerBleeding(out var why)) return;
            _approachAfter = Time.time + Rnd(10f, 20f);
            if (Time.time >= _nextBloodLog)
            {
                _nextBloodLog = Time.time + 30f;
                Dbg($"smells your blood ({why}) - his next charge comes sooner");
            }
        }
    }
}
