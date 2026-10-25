using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace TheStalker
{
    /// <summary>
    /// Full power (Blackout Addons 1.27.10+):
    /// - He still charges, but only when you stand somewhere dark (bright rooms are safe). The game's own tube and panel
    ///   lights count as light, and the ones around him die as he passes (Blackout Addons 1.27.9+).
    /// - A while after the power comes back he may sneak to the generator switch and shut it down (up to twice a raid,
    ///   3 min apart). Stand near the switch and he waits.
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<bool> CutsPower = () => true;

        private const int MaxSabotages = 2;
        private static int _sabotages;
        private static float _powerOnSince = -1f, _sabDelay, _lastSabotage = -1000f, _nextSabCheck, _atSwitchSince = -1f, _nextGuardLog;
        private static bool _sabTeleported;
        private static Vector3 _sabTarget, _switchAt;

        private static void ResetSabotage()
        {
            _sabotages = 0;
            _powerOnSince = -1f;
            _sabDelay = 0f;
            _lastSabotage = -1000f;
            _nextSabCheck = _nextGuardLog = 0f;
            _atSwitchSince = -1f;
            _sabTeleported = false;
            _tubes = null;
            NearTubes.Clear();
            _nextTubeScan = _nextTubeNear = 0f;
        }

        /// <summary>Called while he stalks. True = he's off to the switch now.</summary>
        private static bool MaybeSabotage()
        {
            if (!CutsPower() || !BlackoutLink.Present || Time.time < _nextSabCheck) return false;
            _nextSabCheck = Time.time + 1f;
            if (BlackoutLink.Power < 1)
            {
                _powerOnSince = -1f;
                return false;
            }
            if (_powerOnSince < 0f)
            {
                _powerOnSince = Time.time;
                _sabDelay = Rnd(45f, 100f);
            }
            if (_sabotages >= MaxSabotages || Time.time - _lastSabotage < 180f || Time.time - _powerOnSince < _sabDelay) return false;
            if (_seen || Crashing || !BlackoutLink.CanCut) return false;
            var sw = BlackoutLink.SwitchPosition;
            if (!sw.HasValue) return false;
            if ((_me.Position - sw.Value).magnitude < 12f)
            {
                if (Time.time >= _nextGuardLog)
                {
                    _nextGuardLog = Time.time + 30f;
                    Dbg("wants to cut the power, but you're guarding the switch - he waits");
                }
                return false;
            }
            if (!NavMesh.SamplePosition(sw.Value, out var hit, 2.5f, NavMesh.AllAreas)) return false;
            _switchAt = sw.Value;
            _sabTarget = hit.position;
            _sabTeleported = false;
            _atSwitchSince = -1f;
            SetState(Mode.Sabotage, "the lights are on - he goes to cut the power");
            Log.LogInfo($"[Stalker] heads for the generator switch ({(Body.Position - _sabTarget).magnitude:0} m from him, {(_me.Position - _sabTarget).magnitude:0} m from you).");
            return true;
        }

        private static void DoSabotage(BotOwner b)
        {
            KnifeOut(b);
            if (!BlackoutLink.CanCut || BlackoutLink.Power < 1)
            {
                SetState(Mode.Stalk, "the power is already off");
                return;
            }
            if (Time.time - _stateSince > 120f)
            {
                _lastSabotage = Time.time - 120f;            // try again in a minute
                SetState(Mode.Stalk, "couldn't get to the switch");
                return;
            }
            float meToSwitch = (_me.Position - _sabTarget).magnitude;
            if (meToSwitch < 8f)
            {
                _lastSabotage = Time.time - 150f;            // you're there: try again in half a minute
                SetState(Mode.Stalk, "you're at the switch");
                return;
            }
            float d = (Body.Position - _sabTarget).magnitude;
            // far off, unseen, and you're nowhere near the switch: slip through the dark to the generator room
            if (!_sabTeleported && d > 45f && !_seen && Time.time - _lastSeenByPlayer > 3f && meToSwitch > 40f)
            {
                _sabTeleported = true;
                var spot = SpotNear(_sabTarget, 6f, 14f);
                if (spot.HasValue)
                {
                    try
                    {
                        b.Mover.Teleport(spot.Value);
                        Dbg($"slipped through the dark to the generator room ({(spot.Value - _sabTarget).magnitude:0} m from the switch)");
                        d = (Body.Position - _sabTarget).magnitude;
                    }
                    catch (Exception)
                    {
                        // walk then
                    }
                }
            }
            if (d > 1.4f)
            {
                _atSwitchSince = -1f;
                Move(b, _sabTarget, d > 6f);
                return;
            }
            Hold(b);
            b.Steering.LookToPoint(_switchAt);
            if (_atSwitchSince < 0f) _atSwitchSince = Time.time;
            if (Time.time - _atSwitchSince < 1.2f) return;      // a moment at the panel
            if (!BlackoutLink.RequestCut("the Stalker"))
            {
                _lastSabotage = Time.time - 150f;
                SetState(Mode.Stalk, "the switch didn't respond");
                return;
            }
            _sabotages++;
            _lastSabotage = Time.time;
            Log.LogInfo($"[Stalker] shuts the generators down ({_sabotages} of {MaxSabotages}) - the facility goes dark. You were {meToSwitch:0} m from the switch.");
            Later(1.5f, () => Laugh(_dist < 16f));
            SetState(Mode.Hide, "the lights are out");
            _approachAfter = Time.time + Rnd(10f, 20f);
        }

        /// <summary>A spot near a point (the switch), out of your sight, from where he can walk to it.</summary>
        private static Vector3? SpotNear(Vector3 target, float minR, float maxR)
        {
            var path = new NavMeshPath();
            for (int i = 0; i < 24; i++)
            {
                float a = Rnd(0f, 360f) * Mathf.Deg2Rad;
                Vector3 cand = target + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * Rnd(minR, maxR);
                if (!NavMesh.SamplePosition(cand, out var hit, 3f, NavMesh.AllAreas)) continue;
                var p = hit.position;
                if (Mathf.Abs(p.y - target.y) > 3f) continue;
                if (!Physics.Linecast(_eye, p + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask)) continue;   // you'd see him
                if (!NavMesh.CalculatePath(p, target, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                return p;
            }
            return null;
        }

        // ---------------------------------------------------------------- the game's tube / panel lights

        private static BaseLight[] _tubes;
        private static readonly List<BaseLight> NearTubes = new List<BaseLight>();
        private static float _nextTubeScan, _nextTubeNear;

        /// <summary>Lit by one of the facility's own tube or panel lights (most of Labs at full power)?</summary>
        private static bool TubeLit(Vector3 p)
        {
            if (_me == null) return false;
            if (_tubes == null || Time.time >= _nextTubeScan)
            {
                _nextTubeScan = Time.time + 45f;
                try { _tubes = UnityEngine.Object.FindObjectsOfType<BaseLight>(); }
                catch (Exception) { _tubes = Array.Empty<BaseLight>(); }
            }
            if (Time.time >= _nextTubeNear)
            {
                _nextTubeNear = Time.time + 1f;
                NearTubes.Clear();
                var c = _me.Position;
                foreach (var t in _tubes)
                {
                    if (t != null && (t.transform.position - c).sqrMagnitude < 70f * 70f) NearTubes.Add(t);
                }
            }
            foreach (var t in NearTubes)
            {
                if (t == null || !t.enabled || t.m_Intensity < 0.05f) continue;     // dead (blackout), or killed around him
                float reach = t is TubeLight tl ? Mathf.Min(tl.m_Range * 0.4f, 3.5f) : 3f;     // right under it, not the whole room
                if ((t.transform.position - p).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }
    }
}
