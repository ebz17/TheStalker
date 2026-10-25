using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using UnityEngine;

namespace TheStalker
{
    /// <summary>
    /// His stims (he's a junkie). Two kinds:
    /// - Healing, when he's badly hurt and out of your sight: Zagustin (bleeding), eTG-c / Propital / Perfotoran (low health),
    ///   Trimadol (a leg is gone). Any stim also brings a destroyed leg back and gives a short burst of speed.
    /// - Highs, on purpose: right before a charge he may shoot Obdolbos 2 (berserk for 30 s, then a 10 s crash),
    ///   Adrenaline (a lunge at the start of the charge) or PNB (deeper cuts for 90 s). After being driven off, SJ6 (faster bolts for 2 min).
    /// You hear the injector, then he laughs.
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<int> StimUses = () => 6;
        /// <summary>Extra speed for a few seconds after a stim (read by the speed patch).</summary>
        public static float StimBoost => Time.time < _boostUntil ? 1.15f : 1f;
        /// <summary>His highs: berserk, the adrenaline lunge, SJ6 while he runs (read by the speed patch).</summary>
        public static float JunkieBoost =>
            (Berserk ? 1.2f : 1f) * (Time.time < _lungeUntil ? 1.3f : 1f) * (Time.time < _sj6Until && State == Mode.Flee ? 1.2f : 1f);

        private const string Zagustin = "5c0e533786f7747fa23f4d47";
        private const string Etgc = "5c0e534186f7747fa1419867";
        private const string Trimadol = "637b620db7afa97bfc3d7009";
        private const string Propital = "5c0e530286f7747fa1419862";
        private const string Perfotoran = "637b6251104668754b72f8f9";
        private const string Obdolbos2 = "637b60c3b7afa97bfc3d7001";
        private const string Adrenaline = "5c10c8fd86f7743d7d706df3";
        private const string Pnb = "637b6179104668754b72f8f5";
        private const string Sj6 = "5c0e531d86f7747fa23f4d42";

        private const float BerserkFor = 30f, CrashFor = 10f, PnbFor = 90f, Sj6For = 120f, LungeFor = 4f;

        private static int _stimsUsed;
        private static float _nextStimCheck, _stimStarted = -1f, _boostUntil;
        private static string _stimName, _stimWhy, _stimTpl;
        private static bool _stimOnPurpose;
        private static readonly List<Meds> MedsList = new List<Meds>();

        private static float _seenStimUse = -1f, _healFrom = -1f, _nextHealCheck, _lastInject = -100f, _ourStimUse = -1f;
        private static readonly HashSet<string> UsedStims = new HashSet<string>();

        // highs
        private static float _berserkUntil, _crashUntil, _pnbUntil, _sj6Until, _lungeUntil, _adrenalineAt = -100f;
        private static float _nextStumble, _stumbleUntil;
        private static bool _dosedForCharge, _crashLogged, _hasFled;

        public static bool Berserk => Time.time < _berserkUntil;
        public static bool Crashing => _crashUntil > 0f && Time.time >= _berserkUntil && Time.time < _crashUntil;
        private static bool DeepCuts => Time.time < _pnbUntil;
        private static bool High => Berserk || DeepCuts || Time.time < _sj6Until;

        private static string HighStatus()
        {
            var on = new List<string>();
            if (Berserk) on.Add($"berserk {(_berserkUntil - Time.time):0} s");
            if (Crashing) on.Add($"crashing {(_crashUntil - Time.time):0} s");
            if (DeepCuts) on.Add($"PNB {(_pnbUntil - Time.time):0} s");
            if (Time.time < _sj6Until) on.Add($"SJ6 {(_sj6Until - Time.time):0} s");
            if (Time.time - _adrenalineAt < 25f) on.Add("adrenaline ready");
            return on.Count == 0 ? "sober" : string.Join(", ", on);
        }

        /// <summary>Every second: a stim the game's own AI used (not ours), and how much he has healed.</summary>
        private static void WatchHealing()
        {
            if (Time.time < _nextHealCheck || Bot == null || Body == null) return;
            _nextHealCheck = Time.time + 1f;
            try
            {
                var st = Bot.Medecine?.Stimulators;
                if (st != null && st.LastUseTime > 0f && st.LastUseTime != _seenStimUse)
                {
                    _seenStimUse = st.LastUseTime;
                    if (Mathf.Abs(st.LastUseTime - _ourStimUse) > 0.5f)
                        Log.LogInfo($"[Stalker] the game's own AI made him use a stim ({_stimsUsed} of ours used, {Health():0} HP).");
                }
            }
            catch (Exception)
            {
                // logging only
            }
            float hp = Health();
            if (_healFrom < 0f || hp < _healFrom) _healFrom = hp;
            else if (hp - _healFrom >= 50f)
            {
                Log.LogInfo($"[Stalker] healed {hp - _healFrom:0} HP ({_healFrom:0} -> {hp:0} of {_maxHealth:0}).");
                _healFrom = hp;
            }
        }

        private static void ResetStims()
        {
            _seenStimUse = _healFrom = _ourStimUse = -1f;
            _lastInject = -100f;
            UsedStims.Clear();
            _nextHealCheck = 0f;
            _stimsUsed = 0;
            _nextStimCheck = _boostUntil = 0f;
            _stimStarted = -1f;
            _stimName = _stimWhy = _stimTpl = null;
            _stimOnPurpose = false;
            _berserkUntil = _crashUntil = _pnbUntil = _sj6Until = _lungeUntil = 0f;
            _adrenalineAt = -100f;
            _nextStumble = _stumbleUntil = 0f;
            _dosedForCharge = _crashLogged = _hasFled = false;
        }

        /// <summary>True while he's injecting: the caller lets him stand still (no knife swap, or the stim is cancelled).</summary>
        private static bool Medicate(BotOwner b)
        {
            var st = b.Medecine?.Stimulators;
            if (st == null) return false;

            if (_stimStarted >= 0f)
            {
                if (st.Using && Time.time - _stimStarted < 10f)
                {
                    b.Sprint(false);
                    b.StopMove();
                    return true;
                }
                if (st.Using)
                {
                    // never finished (hands were busy): give up on this one
                    st.Using = false;
                    Dbg($"the {_stimName} didn't go in - giving up on it");
                    try { b.WeaponManager.Selector.TakePrevWeapon(); } catch (Exception) { }
                }
                _stimStarted = -1f;
                return false;
            }

            if (Time.time < _nextStimCheck) return false;
            _nextStimCheck = Time.time + 1f;
            if (Time.time - _lastInject < 8f) return false;        // one at a time: let the last one work first
            if (_stimsUsed >= StimUses()) return false;
            if (State != Mode.Hide && State != Mode.Flee && State != Mode.Stalk) return false;
            if (_seen || Time.time - _lastSeenByPlayer < 2f || _dist < 12f) return false;

            var need = Need();
            if (need != null)
            {
                var heal = FindStim(need.Value.tpls);
                if (heal != null) return Inject(b, heal, need.Value.why, onPurpose: false);
            }
            // driven off and hiding: SJ6 so the next bolt is faster
            if (State == Mode.Hide && _hasFled && Time.time >= _sj6Until)
            {
                var sj6 = FindStim(new[] { Sj6 });
                if (sj6 != null) return Inject(b, sj6, "to run faster", onPurpose: true);
            }
            return false;
        }

        /// <summary>Right before a charge (called once per charge): a high first, sometimes. True = injecting now, the charge waits.</summary>
        private static bool PreChargeDose(BotOwner b)
        {
            if (_dosedForCharge) return false;
            _dosedForCharge = true;
            if (_stimStarted >= 0f || _stimsUsed >= StimUses() || Time.time - _lastInject < 8f) return false;
            if (_seen || _dist < 10f || Crashing) return false;
            if (Rng.Next(100) >= 70) return false;
            var order = new List<string> { Adrenaline };
            if (!Berserk) order.Add(Obdolbos2);
            if (!DeepCuts) order.Add(Pnb);
            var stim = FindStim(order.OrderBy(_ => Rng.Next()).ToArray());
            if (stim == null) return false;
            var st = b.Medecine?.Stimulators;
            return st != null && Inject(b, stim, "before a charge", onPurpose: true);
        }

        private static bool Inject(BotOwner b, Stimulator stim, string why, bool onPurpose)
        {
            var st = b.Medecine?.Stimulators;
            if (st == null) return false;
            _stimName = stim.ShortName.Localized();
            _stimWhy = why;
            _stimTpl = stim.TemplateId.ToString();
            _stimOnPurpose = onPurpose;
            try
            {
                st._stimulator = stim;
                st.HaveSmt = true;
                _stimStarted = Time.time;
                _lastInject = Time.time;
                UsedStims.Add(stim.Id);                              // never the same injector twice (it can linger a moment after use)
                st.TryApply(true, null, ok => StimDone(ok));
                _ourStimUse = st.LastUseTime;
                if (!st.Using)
                {
                    _stimStarted = -1f;
                    _nextStimCheck = Time.time + 3f;
                    return false;
                }
                Log.LogInfo($"[Stalker] injects {_stimName} ({_stimWhy}, {Health():0} of {_maxHealth:0} HP, {_dist:0} m from you).");
                b.Sprint(false);
                b.StopMove();
                return true;
            }
            catch (Exception ex)
            {
                _stimStarted = -1f;
                _nextStimCheck = Time.time + 5f;
                Dbg($"couldn't use a stim: {ex.Message}");
                return false;
            }
        }

        /// <summary>Which healing stim he wants right now (in order of preference), or null if he's fine.</summary>
        private static (string[] tpls, string why)? Need()
        {
            var hc = Body.HealthController;
            float hp = Health();
            bool legGone = hc.IsBodyPartDestroyed(EBodyPart.LeftLeg) || hc.IsBodyPartDestroyed(EBodyPart.RightLeg);
            bool heavyBleed = false, lightBleed = false;
            try
            {
                foreach (var e in hc.GetAllActiveEffects())
                {
                    if (e is IHeavyBleeding) heavyBleed = true;
                    else if (e is ILightBleeding) lightBleed = true;
                }
            }
            catch (Exception)
            {
                // no effect info: go by health alone
            }
            bool low = _maxHealth > 0f && hp < _maxHealth * 0.4f;

            if (heavyBleed || (lightBleed && hp < _maxHealth * 0.6f)) return (new[] { Zagustin, Etgc, Propital, Perfotoran, Trimadol }, "bleeding");
            if (low) return (new[] { Etgc, Propital, Perfotoran, Zagustin, Trimadol }, "badly hurt");
            if (legGone) return (new[] { Trimadol, Propital, Etgc, Perfotoran, Zagustin }, "a leg is gone");
            return null;
        }

        private static Stimulator FindStim(string[] tpls)
        {
            MedsList.Clear();
            try
            {
                Body.InventoryController.GetAcceptableItemsNonAlloc(BotMedecine.anySlots, MedsList);
            }
            catch (Exception)
            {
                return null;
            }
            var stims = MedsList.OfType<Stimulator>().ToList();
            foreach (var tpl in tpls)
            {
                var s = stims.FirstOrDefault(x => x.TemplateId.ToString() == tpl && !UsedStims.Contains(x.Id));
                if (s != null) return s;
            }
            return null;
        }

        private static void StimDone(bool ok)
        {
            _stimStarted = -1f;
            if (Body == null || !Body.HealthController.IsAlive) return;
            if (!ok)
            {
                Dbg($"the {_stimName} didn't go in");
                return;
            }
            _stimsUsed++;
            var fixedParts = new List<string>();
            try
            {
                var ahc = Body.ActiveHealthController;
                foreach (var part in new[] { EBodyPart.LeftLeg, EBodyPart.RightLeg, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.Stomach })
                {
                    if (ahc.IsBodyPartDestroyed(part) && ahc.RestoreBodyPart(part, 0.8f)) fixedParts.Add(part.ToString());
                }
            }
            catch (Exception ex)
            {
                Dbg($"couldn't fix his legs: {ex.Message}");
            }
            _boostUntil = Time.time + 8f;
            _lastHealth = Health();          // healing isn't a hit
            string high = ApplyHigh(_stimTpl);
            Log.LogInfo($"[Stalker] {_stimName} is in ({_stimsUsed} of {StimUses()} used){(fixedParts.Count > 0 ? ", fixed: " + string.Join(", ", fixedParts) : "")}, {Health():0} HP now{high}.");
            if (_stimOnPurpose) Later(0.6f, () => Laugh(_dist < 16f));
            else Voice("after a stim", ETagStatus.Unaware, EPhraseTrigger.OnMutter, EPhraseTrigger.OnEnemyConversation);
        }

        private static string ApplyHigh(string tpl)
        {
            float now = Time.time;
            switch (tpl)
            {
                case Obdolbos2:
                    _berserkUntil = now + BerserkFor;
                    _crashUntil = _berserkUntil + CrashFor;
                    _crashLogged = false;
                    return $" - BERSERK for {BerserkFor:0} s: light, looks and bullets won't turn him back, then he crashes";
                case Adrenaline:
                    _adrenalineAt = now;
                    if (State == Mode.Approach) Lunge();
                    return " - adrenaline: he lunges when the charge starts";
                case Pnb:
                    _pnbUntil = now + PnbFor;
                    return $" - PNB: deeper cuts for {PnbFor:0} s";
                case Sj6:
                    _sj6Until = now + Sj6For;
                    return $" - SJ6: faster bolts for {Sj6For / 60f:0} min";
                default:
                    return "";
            }
        }

        private static void Lunge()
        {
            if (Time.time - _adrenalineAt > 25f) return;
            _adrenalineAt = -100f;
            _lungeUntil = Time.time + LungeFor;
            Dbg($"lunges at you (adrenaline, {LungeFor:0} s)");
        }

        /// <summary>Every frame: the berserk high running out into a crash.</summary>
        private static void JunkieTick()
        {
            if (_crashUntil <= 0f) return;
            if (Crashing && !_crashLogged)
            {
                _crashLogged = true;
                Log.LogInfo($"[Stalker] crashes: the Obdolbos wears off - slow and stumbling for {CrashFor:0} s. Now's your chance.");
                Voice("crashing", ETagStatus.Combat, EPhraseTrigger.OnAgony, EPhraseTrigger.HurtHeavy, EPhraseTrigger.OnBeingHurt);
                if (State == Mode.Approach || State == Mode.Attack) Leave("the high wears off");
            }
            else if (Time.time >= _crashUntil)
            {
                _crashUntil = _berserkUntil = 0f;
                Dbg("back on his feet after the crash");
            }
        }

        /// <summary>While crashing: slow, and every couple of seconds he stumbles to a stop. True = he stumbles now (don't move).</summary>
        private static bool CrashStep(BotOwner b)
        {
            if (!Crashing)
            {
                b.SetTargetMoveSpeed(1f);
                return false;
            }
            b.SetTargetMoveSpeed(0.45f);
            if (Time.time >= _nextStumble)
            {
                _nextStumble = Time.time + Rnd(2f, 3.5f);
                _stumbleUntil = Time.time + 0.7f;
            }
            if (Time.time < _stumbleUntil)
            {
                b.Sprint(false);
                b.StopMove();
                b.SetPose(0.6f);
                return true;
            }
            return false;
        }
    }
}
