using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.AI;

namespace TheStalker
{
    internal enum Mode
    {
        None,
        Hide,       // just arrived / after running: wait somewhere dark and out of sight
        Stalk,      // shadow the player from behind, laugh now and then
        Approach,   // rush the player's back
        Attack,     // knife
        Flee,       // seen, lit, hurt or after a hit: run off into the dark
        Leave,      // Killa is here: go away and vanish
        Sabotage,   // the lights are on: go to the generator switch and shut the power down
        Gone,       // about to be removed: stand still (keeps the Cultist brain out)
    }

    /// <summary>The Stalker himself: wake-up, perception and what he does each frame.</summary>
    internal static partial class Stalker
    {
        public const int Role = 848470;
        public static ManualLogSource Log;
        public static StalkerAudio Audio;
        public static Action<float, Action> Later = (d, a) => a();

        // settings (wired by the plugin)
        public static Func<bool> Enabled = () => true;
        public static Func<int> Chance = () => 35;
        public static Func<float> WakeMinutes = () => 10f;
        public static Func<float> StandaloneMinutes = () => 12f;
        public static Func<int> Bleeds = () => 3;
        public static Func<bool> Poison = () => true;
        public static Func<float> BlindSeconds = () => 4f;
        public static Func<int> BlindChance = () => 50;
        private static bool _blindThisAttack;
        public static Func<float> SpeedMult = () => 1.35f;
        public static Func<bool> FlashlightScares = () => true;
        public static Func<bool> LeaveOnKilla = () => false;
        public static Func<bool> Russian = () => true;
        public static Func<bool> DebugLog = () => true;
        public static Func<bool> CultistVoice = () => true;
        public static Func<float> CommitDistance = () => 8f;
        public static Func<bool> KillsLights = () => true;
        private static bool _presenceLogged;
        private static bool _committed;      // inside commit range: he keeps coming whatever you do

        public static BotOwner Bot { get; private set; }
        public static Player Body { get; private set; }
        public static Mode State { get; private set; } = Mode.None;
        public static bool Sprinting;                  // read by the speed patch
        public static float PanicUntil;                // first seconds of running away: walking counts as sprinting
        /// <summary>Charging at you: walking counts as sprinting the whole way (the game holds bots back from sprinting near turns and doors).</summary>
        public static bool Charging => State == Mode.Approach;
        /// <summary>His charge / bolt speed in m/s (you sprint at roughly 6-7).</summary>
        public static Func<float> ChargeSpeed = () => 7.5f;
        private static float _chargeStart, _chargeDist, _chargeDoorTime;
        private static int _chargeFrames, _chargeSprintFrames;
        public static int ChargeBoosted, ChargeSteps, ChargeNoRun, ChargeStill, ChargeClipped;
        public static float ChargeBase;
        private static Vector3 _chargeLast;
        private static float _fleeCheckAt = -1f;
        private static Vector3 _fleeFrom;

        private static readonly System.Random Rng = new System.Random();
        private static string _profileId;
        private static bool _needsPlacement, _driven, _fleeAfterAttack;
        private static string _killer;
        private static float _hideFor = 8f, _seenSince = -1f, _ignoreSightUntil, _hurtCooldown, _maxHealth;
        private static float _nextDiag, _foundAt;
        private static bool _inRaid, _rolled, _inThisRaid, _announced, _deathLogged, _marked, _whispered, _hitLanded;
        private static float _raidStart, _stateSince, _nextLook, _nextPerception, _nextSpot, _nextLaugh, _approachAfter;
        private static float _nextMarkCheck, _lastHealth, _scareCooldown, _nextLightCache, _nextSceneLights, _lastSeenByPlayer = -100f;
        private static float _farSince = -1f, _hiddenSince = -1f;
        private static Vector3? _spot;
        private static Player _me;
        private static Vector3 _eye, _fwd;
        private static float _dist;
        private static bool _seen, _litByPlayer, _playerInLight;
        private static readonly List<Light> PlayerLights = new List<Light>();
        private static readonly List<Light> SceneLights = new List<Light>();
        private const string AdminKey = "6a33c17933cff6b88c08902e";

        public static bool IsStalker(Player p)
        {
            try
            {
                return p != null && p.IsAI && (int)p.Profile.Info.Settings.Role == Role;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Every frame while he's about: Blackout Addons makes the emergency lamps around him die.</summary>
        private static void DarkPresence()
        {
            bool about = Bot != null && Body != null && Body.HealthController.IsAlive && State != Mode.None && State != Mode.Gone && KillsLights();
            if (!about) return;
            bool ok = BlackoutLink.SetPresence(Body.Position);
            if (!_presenceLogged)
            {
                _presenceLogged = true;
                Log.LogInfo(ok ? "[Stalker] the lights around him die (Blackout Addons)." : "[Stalker] dying lights need Blackout Addons 1.27.5+ - skipped.");
            }
        }

        public static bool Controls(BotOwner b) => b != null && Bot == b && State != Mode.None && Body != null && Body.HealthController.IsAlive;

        private static void Dbg(string m)
        {
            if (DebugLog()) Log.LogInfo("[Stalker] " + m);
        }

        private static void SetState(Mode m, string why)
        {
            if (State == m) return;
            Dbg($"{State} -> {m} ({why}), {_dist:0} m from you");
            if (State == Mode.Approach) LogChargeSpeed();
            State = m;
            _stateSince = Time.time;
            _spot = null;
            _nextSpot = 0f;
            _whispered = false;
            if (m != Mode.Attack) _committed = false;
            if (m == Mode.Hide) _hideFor = Rnd(6f, 12f);
            if (m == Mode.Approach)
            {
                NewCharge(unseen: !_seen && !_litByPlayer);
                _chargeStart = Time.time;
                _chargeDoorTime = 0f;
                _chargeDist = 0f;
                _chargeFrames = _chargeSprintFrames = 0;
                ChargeBoosted = ChargeSteps = ChargeNoRun = ChargeStill = ChargeClipped = 0;
                ChargeBase = 0f;
                _chargeLast = Body != null ? Body.Position : Vector3.zero;
                try { Body?.MovementContext?.SetPoseLevel(1f, true); } catch (Exception) { }
                _dosedForCharge = false;             // the next charge may get its own high
                _ignoreLight = false;
                Lunge();
            }
            if (m == Mode.Flee)
            {
                _hasFled = true;
                NoteDrivenOff(why);
                // bolt: stand up now, and full speed from the first step (the game takes a moment to let a bot sprint)
                PanicUntil = Time.time + 2f;
                _fleeCheckAt = Time.time + 1f;
                _fleeFrom = Body != null ? Body.Position : Vector3.zero;
                try { Body?.MovementContext?.SetPoseLevel(1f, true); } catch (Exception) { }
            }
        }

        private static float Rnd(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

        // ---------------------------------------------------------------- raid / wake-up

        public static void Tick()
        {
            var gw = Singleton<GameWorld>.Instance;
            var me = gw?.MainPlayer;
            if (me == null || gw.LocationId == null)
            {
                if (_inRaid) ResetRaid();
                return;
            }
            if (!_inRaid)
            {
                _inRaid = true;
                _raidStart = Time.time;
                try { Audio?.VerifyAll(); } catch (Exception ex) { Log.LogWarning($"[Stalker] sound check failed: {ex.Message}"); }
                _me = me;
                me.BeingHitAction += OnMeHit;
            }
            if (!Enabled() || !MapAllowed(gw)) return;

            if (!_rolled && Time.time - _raidStart > 3f)
            {
                _rolled = true;
                int roll = Rng.Next(100);
                _inThisRaid = roll < Chance();
                string when = !_onLabs ? $"{_wakeAfter / 60f:0} min into the raid"
                    : BlackoutLink.Present ? $"if the lights go out: when the power control burns out or after {WakeMinutes():0} min in the dark"
                    : $"after {StandaloneMinutes():0} min (Blackout Addons not installed)";
                Log.LogInfo(_inThisRaid
                    ? $"[Stalker] he is in this raid on {_mapName} (rolled {roll} < {Chance()}%). He wakes {when}."
                    : $"[Stalker] not this raid on {_mapName} (rolled {roll}, chance {Chance()}%).");
            }

            if (_inThisRaid && StalkerSpawn.Held && Time.time - _raidStart > 15f)
            {
                string why = null;
                if (!_onLabs)
                {
                    if (Time.time - _raidStart >= _wakeAfter) why = $"{_wakeAfter / 60f:0} min into the raid on {_mapName}";
                }
                else if (BlackoutLink.Present)
                {
                    float dark = BlackoutLink.DarkSince;
                    if (BlackoutLink.BurntAt >= 0f && BlackoutLink.BurntAt > _raidStart) why = "the power control burnt out";
                    else if (BlackoutLink.Power == 0 && dark > _raidStart && Time.time - dark >= WakeMinutes() * 60f) why = $"{WakeMinutes():0} min in the dark";
                }
                else if (Time.time - _raidStart >= StandaloneMinutes() * 60f)
                {
                    why = $"{StandaloneMinutes():0} min into the raid";
                }
                if (why != null) StalkerSpawn.Release(why);
            }

            if (Bot == null && StalkerSpawn.Released) FindHim(gw);
            if (Bot != null) Watch();
            if (Bot != null) WatchHealing();
            DarkPresence();
            GunFlickerTick();
        }

        /// <summary>Test key: wake him now, whatever the roll said.</summary>
        public static void WakeNow()
        {
            if (!_inRaid) return;
            _inThisRaid = true;
            _mapChecked = _mapOk = true;             // any map, any time of day
            if (!StalkerSpawn.Release("test key")) Log.LogInfo($"[Stalker] test key: {(StalkerSpawn.Released ? "he's already awake" : "no wave held (is the server mod installed?)")}.");
        }

        private static void FindHim(GameWorld gw)
        {
            foreach (var p in gw.AllAlivePlayersList)
            {
                if (!IsStalker(p) || p.AIData?.BotOwner == null) continue;
                Bot = p.AIData.BotOwner;
                Body = p;
                _profileId = p.ProfileId;
                _lastHealth = _maxHealth = Health();
                _killer = null;
                p.OnPlayerDead += (player, aggressor, dmg, part) =>
                {
                    try { _killer = (aggressor == null ? "nobody" : aggressor.IsYourPlayer ? "you" : aggressor.Profile?.Nickname) + $", {dmg.DamageType}, {part}"; }
                    catch (Exception) { _killer = "?"; }
                };
                p.BeingHitAction += (dmg, part, val) =>
                {
                    try
                    {
                        var by = dmg.Player?.iPlayer;
                        string who = by == null ? "nobody" : by.IsYourPlayer ? "you" : by.Profile?.Nickname;
                        Log.LogInfo($"[Stalker] hit by {who}: {dmg.DamageType} to the {part}, {dmg.Damage:0} damage.");
                    }
                    catch (Exception)
                    {
                        // logging only
                    }
                };
                SetState(Mode.Hide, "arrived");
                Log.LogInfo($"[Stalker] is in the raid at {p.Position:F1} ({p.Profile.Nickname}).");
                _needsPlacement = true;
                _driven = false;
                _approachAfter = Time.time + Rnd(25f, 50f);
                _foundAt = Time.time;
                _nextDiag = Time.time + 3f;
                if (!_announced)
                {
                    _announced = true;
                    if (_onLabs) Later(Rnd(5f, 9f), () => Audio?.Play2D(Russian() ? "vo_stalker_ru" : "vo_stalker_en", 0.9f));   // the Labs PA
                    else Later(Rnd(5f, 9f), () => Laugh(false));                                                                  // elsewhere: a laugh in the dark
                }
                return;
            }
        }

        private static float Health()
        {
            try
            {
                var hc = Body.HealthController;
                float sum = 0f;
                foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
                {
                    if (part == EBodyPart.Common) continue;
                    sum += hc.GetBodyPartHealth(part).Current;
                }
                return sum;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static void Watch()
        {
            if (Body == null || !Body.HealthController.IsAlive)
            {
                if (!_deathLogged)
                {
                    _deathLogged = true;
                    Log.LogInfo($"[Stalker] is dead (killed by {_killer ?? "?"}).");
                }
                State = Mode.None;
                return;
            }
            if (!_driven && Bot.BotState == EBotState.PreActive && Time.time - _foundAt > 6f)
            {
                try
                {
                    if (Bot.WeaponManager != null && !Bot.WeaponManager.IsReady)
                    {
                        // the game waits for a ready gun before it switches a bot on; he may only have his knife in hand
                        Bot.WeaponManager.IsReady = true;
                        Log.LogWarning("[Stalker] his gun never got ready - switching him on anyway.");
                    }
                }
                catch (Exception)
                {
                    // try again next tick
                }
            }
            if (!_driven && Time.time >= _nextDiag && Time.time - _foundAt < 180f)
            {
                _nextDiag = Time.time + 5f;
                string brain;
                try { brain = Bot.Brain?.BaseBrain?.ShortName() ?? "none"; } catch (Exception) { brain = "?"; }
                string layer;
                try { layer = Bot.Brain?.ActiveLayerName() ?? "none"; } catch (Exception) { layer = "?"; }
                Log.LogWarning($"[Stalker] not driving yet: bot state {Bot.BotState}, brain {brain}, layer {layer}, at {Body.Position:F1}, {(Body.Position - _me.Position).magnitude:0} m from you.");
            }
            if (Time.time >= _nextMarkCheck)
            {
                _nextMarkCheck = Time.time + 3f;
                bool marked = HasAdminKey();
                if (marked != _marked)
                {
                    _marked = marked;
                    Log.LogInfo(marked ? "[Stalker] you took Admin's key - he's marked you." : "[Stalker] you no longer carry Admin's key.");
                    if (marked) Later(Rnd(2f, 5f), () => Laugh(true));
                }
            }
        }

        private static bool HasAdminKey()
        {
            try
            {
                foreach (var item in _me.InventoryController.Inventory.GetPlayerItems(EPlayerItems.Equipment))
                {
                    if (item != null && item.TemplateId.ToString() == AdminKey) return true;
                }
            }
            catch (Exception)
            {
                // inventory not ready
            }
            return false;
        }

        public static void ResetRaid()
        {
            try { if (_me != null) _me.BeingHitAction -= OnMeHit; } catch (Exception) { }
            _me = null;
            _inRaid = _rolled = _inThisRaid = _announced = _deathLogged = _marked = _needsPlacement = _presenceLogged = false;
            Bot = null;
            Body = null;
            _profileId = null;
            State = Mode.None;
            Sprinting = false;
            _spot = null;
            _farSince = _hiddenSince = _seenSince = -1f;
            _nextStalkLog = 0f;
            _ignoreSightUntil = _hurtCooldown = 0f;
            _fleeAfterAttack = false;
            ResetStims();
            ResetRetreat();
            ResetDoors();
            ResetMaps();
            ResetSabotage();
            StopGunFlicker();
            _nextFlick = 0f;
            PlayerLights.Clear();
            SceneLights.Clear();
            LampRange.Clear();
            _litChangeSince = -1f;
            _nextLightCache = _nextSceneLights = 0f;
            StalkerSpawn.ResetRaid();
        }

        // ---------------------------------------------------------------- perception

        private static Vector3 Chest(Player p) => p.Position + Vector3.up * 1.25f;

        private static void Perceive()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                _eye = cam.transform.position;
                _fwd = cam.transform.forward;
            }
            else
            {
                _eye = _me.Position + Vector3.up * 1.6f;
                _fwd = _me.Transform.forward;
            }
            Vector3 him = Chest(Body);
            _dist = (Body.Position - _me.Position).magnitude;
            bool look = CanPlayerSee(him, VisibleRange(him), 30f) && Body.HealthController.IsAlive;
            if (look) { if (_seenSince < 0f) _seenSince = Time.time; }
            else _seenSince = -1f;
            _seen = look && Time.time - _seenSince >= 0.35f && Time.time >= _ignoreSightUntil;
            if (_seen) _lastSeenByPlayer = Time.time;
            _litByPlayer = FlashlightScares() && LitByPlayer(him);
            // in the light / out of it only counts once it has held for 0.7 s (a flickering lamp doesn't flip him back and forth)
            bool lit = IsLit(_me.Position + Vector3.up);
            if (lit != _playerInLight)
            {
                if (_litChangeSince < 0f) _litChangeSince = Time.time;
                if (Time.time - _litChangeSince >= 0.7f) { _playerInLight = lit; _litChangeSince = -1f; }
            }
            else _litChangeSince = -1f;
        }

        private static bool Clear(Vector3 a, Vector3 b) => !Physics.Linecast(a, b, LayersMaskController.HighPolyWithTerrainMask);

        /// <summary>In the player's view (about 55 degrees off centre) and nothing in between.</summary>
        private static bool CanPlayerSee(Vector3 p, float maxDist, float cone = 55f)
        {
            Vector3 d = p - _eye;
            if (d.magnitude > maxDist) return false;
            if (Vector3.Angle(_fwd, d) > cone) return false;
            return Clear(_eye, p);
        }

        /// <summary>How far you can make him out: not far in the dark, unless he's lit or you have NVG/thermal on.</summary>
        private static float VisibleRange(Vector3 him)
        {
            if (!DarkMatters) return 40f;
            if (_litByPlayer || IsLit(him)) return 40f;
            try
            {
                if (_me.NightVisionObserver?.Component?.Togglable != null && _me.NightVisionObserver.Component.Togglable.On) return 35f;
                if (_me.ThermalVisionObserver?.Component?.Togglable != null && _me.ThermalVisionObserver.Component.Togglable.On) return 35f;
            }
            catch (Exception)
            {
                // no headgear info
            }
            return 12f;
        }

        private static bool LitByPlayer(Vector3 p)
        {
            if (Time.time >= _nextLightCache)
            {
                _nextLightCache = Time.time + 0.75f;
                PlayerLights.Clear();
                try
                {
                    // your weapon in first person isn't under your body, so look in the hands too (and the body for headlamps)
                    var found = new List<Light>(_me.GetComponentsInChildren<Light>(false));
                    var hands = _me.HandsController?.ControllerGameObject;
                    if (hands != null) found.AddRange(hands.GetComponentsInChildren<Light>(false));
                    foreach (var l in found)
                    {
                        if (l != null && l.enabled && l.type != LightType.Directional && l.intensity > 0.05f && l.range > 2f && !PlayerLights.Contains(l)) PlayerLights.Add(l);
                    }
                }
                catch (Exception)
                {
                    // ignore
                }
            }
            foreach (var l in PlayerLights)
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                Vector3 d = p - l.transform.position;
                if (d.magnitude > Mathf.Max(8f, l.range * 1.2f)) continue;
                if (Vector3.Angle(l.transform.forward, d) > Mathf.Max(12f, l.spotAngle * 0.6f)) continue;
                if (Clear(l.transform.position, p)) return true;
            }
            return false;
        }

        /// <summary>Near a working lamp (not the red beacons, not anyone's flashlight).</summary>
        /// <summary>Lamps only mean something while the facility is dark (Blackout Addons, power out or half).</summary>
        // any blackout raid: dark or full power, he goes by where the light actually is (bright rooms are safe at full power)
        private static bool DarkMatters => BlackoutLink.Present && BlackoutLink.Power >= 0;

        private static float _litChangeSince = -1f;
        private static readonly Dictionary<Light, float> LampRange = new Dictionary<Light, float>();

        private static bool IsLit(Vector3 p)
        {
            if (!DarkMatters) return false;
            if (Time.time >= _nextSceneLights)
            {
                _nextSceneLights = Time.time + 45f;
                SceneLights.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (l == null || !l.enabled || l.intensity < 0.25f || l.type == LightType.Directional) continue;
                    var c = l.color;
                    if (c.r > c.g * 2f && c.r > c.b * 2f) continue;           // red beacons / emergency red
                    if (l.GetComponentInParent<Player>() != null) continue;     // flashlights
                    SceneLights.Add(l);
                    // its real reach (Blackout Addons shrinks the range of the lamps he's killing)
                    LampRange[l] = LampRange.TryGetValue(l, out var r0) ? Mathf.Max(r0, l.range) : l.range;
                }
            }
            if (TubeLit(p)) return true;
            bool kills = KillsLights() && Body != null && BlackoutLink.Present;
            Transform hands = null;
            try { hands = _me.HandsController?.ControllerGameObject?.transform; } catch (Exception) { }
            foreach (var l in SceneLights)
            {
                if (l == null || !l.enabled || l.intensity < 0.25f) continue;
                var lp = l.transform.position;
                // your own weapon light / headlamp is not a lamp you're standing under
                if ((hands != null && l.transform.IsChildOf(hands)) || (lp - _eye).sqrMagnitude < 1.5f * 1.5f) continue;
                if (kills && (lp - Body.Position).sqrMagnitude < 5f * 5f) continue;     // a lamp he's standing under is dead
                float range = LampRange.TryGetValue(l, out var r) ? Mathf.Max(r, l.range) : l.range;
                float reach = Mathf.Min(range * 0.6f, 4f + l.intensity * 2f);
                if ((lp - p).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }

        /// <summary>A dark spot on the navmesh, out of the player's sight, around the player.</summary>
        private static Vector3? FindSpot(float minR, float maxR, bool behind, bool needPath, int tries = 24, bool awayFromPlayer = false)
        {
            return FindSpot(minR, maxR, behind, needPath, tries, awayFromPlayer, true) ?? FindSpot(minR, maxR, behind, needPath, tries / 2, awayFromPlayer, false);
        }

        private static Vector3? _meOnMesh;

        private static Vector3? FindSpot(float minR, float maxR, bool behind, bool needPath, int tries, bool awayFromPlayer, bool darkOnly)
        {
            if (_me == null) return null;
            Vector3 center = _me.Position;
            Vector3 back = -new Vector3(_fwd.x, 0f, _fwd.z).normalized;
            if (back.sqrMagnitude < 0.1f) back = -_me.Transform.forward;
            Vector3? best = null;
            float bestScore = float.MinValue;
            var path = new NavMeshPath();
            _meOnMesh = NavMesh.SamplePosition(center, out var meHit, 2f, NavMesh.AllAreas) ? meHit.position : (Vector3?)null;
            for (int i = 0; i < tries; i++)
            {
                Vector3 dir;
                if (awayFromPlayer && Body != null)
                {
                    Vector3 away = (Body.Position - center);
                    away.y = 0f;
                    dir = Quaternion.Euler(0f, Rnd(-60f, 60f), 0f) * (away.sqrMagnitude > 0.1f ? away.normalized : back);
                }
                else if (behind)
                {
                    dir = Quaternion.Euler(0f, Rnd(-75f, 75f), 0f) * back;
                }
                else
                {
                    float a = Rnd(0f, 360f);
                    dir = new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0f, Mathf.Sin(a * Mathf.Deg2Rad));
                }
                Vector3 cand = center + dir * Rnd(minR, maxR);
                if (!NavMesh.SamplePosition(cand, out var hit, 4f, NavMesh.AllAreas)) continue;
                Vector3 p = hit.position;
                if (Mathf.Abs(p.y - center.y) > 4f) continue;                         // same floor
                if (!Physics.Linecast(_eye, p + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask)) continue;   // you'd see him
                if (darkOnly && IsLit(p + Vector3.up)) continue;
                if (needPath && Body != null)
                {
                    if (!NavMesh.CalculatePath(Body.Position, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                }
                else if (!needPath)
                {
                    // a teleport: the spot must be walkable from where you are - not a roof, a shelf or a ledge he'd drop off
                    if (!_meOnMesh.HasValue || !NavMesh.CalculatePath(_meOnMesh.Value, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                }
                float score = -Mathf.Abs((p - center).magnitude - (minR + maxR) / 2f);
                if (!awayFromPlayer) score += Vector3.Dot((p - center).normalized, back) * 6f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- behaviour (called by the BigBrain layer every frame)

        public static void Drive(BotOwner b)
        {
            if (b != Bot || _me == null || Body == null) return;
            if (!_driven)
            {
                _driven = true;
                Log.LogInfo($"[Stalker] his brain is running ({Time.time - _foundAt:0.0} s after he appeared).");
            }
            if (Time.time >= _nextPerception)
            {
                _nextPerception = Time.time + 0.1f;
                Perceive();
                WatchCharge();
                Interrupts();
            }
            if (_needsPlacement)
            {
                _needsPlacement = false;
                var spot = FindSpot(25f, 40f, behind: true, needPath: false, tries: 40);
                if (spot.HasValue && !_seen)
                {
                    try
                    {
                        b.Mover.Teleport(spot.Value);
                        Dbg($"moved out of sight to {spot.Value:F1}, {(spot.Value - _me.Position).magnitude:0} m from you");
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"[Stalker] couldn't move him: {ex.Message}");
                    }
                }
                else
                {
                    Dbg("no hidden spot found yet - he starts where he spawned");
                }
            }
            Sprinting = false;
            _atDoorNow = false;
            JunkieTick();
            if (Medicate(b)) return;      // injecting a stim: stand still until it's in
            switch (State)
            {
                case Mode.Hide: DoHide(b); break;
                case Mode.Stalk: DoStalk(b); break;
                case Mode.Approach: DoApproach(b); break;
                case Mode.Attack: DoAttack(b); break;
                case Mode.Flee: DoFlee(b); break;
                case Mode.Leave: DoLeave(b); break;
                case Mode.Sabotage: DoSabotage(b); break;
                case Mode.Gone: b.Sprint(false); b.StopMove(); break;
            }
        }

        private static void Interrupts()
        {
            if (State == Mode.Leave || State == Mode.Gone || State == Mode.None) return;
            if (LeaveOnKilla() && BlackoutLink.KillaAt > _raidStart)
            {
                SetState(Mode.Leave, "Killa broke in");
                return;
            }
            // a real hit only: bleeding ticks (a fraction of a point every second) don't scare him off
            float hp = Health();
            float lost = _lastHealth - hp;
            _lastHealth = hp;
            if (lost >= 6f)
            {
                Log.LogInfo($"[Stalker] took a hit: -{lost:0} HP, {hp:0} of {_maxHealth:0} left.");
                if (Berserk && !LegGone())
                {
                    // berserk: a bullet only makes him angry
                    if (Time.time >= _hurtCooldown)
                    {
                        _hurtCooldown = Time.time + 8f;
                        Voice("hurt", ETagStatus.Combat, EPhraseTrigger.OnBeingHurt, EPhraseTrigger.HurtMedium, EPhraseTrigger.HurtLight);
                    }
                    if (State == Mode.Stalk || State == Mode.Hide || State == Mode.Sabotage)
                    {
                        Cornered("berserk - your shot only made him angry");
                        return;
                    }
                }
                else if ((_committed || Enraged) && !LegGone())
                {
                    // committed (or enraged): he takes it and keeps coming
                    if (Time.time >= _hurtCooldown)
                    {
                        _hurtCooldown = Time.time + 8f;
                        Voice("hurt", ETagStatus.Combat, EPhraseTrigger.OnBeingHurt, EPhraseTrigger.HurtMedium, EPhraseTrigger.HurtLight);
                    }
                }
                else if (_dist < SpotChargeDistance && !LegGone() && (State == Mode.Stalk || State == Mode.Hide || State == Mode.Approach || State == Mode.Sabotage))
                {
                    _hurtCooldown = Time.time + 8f;
                    Voice("hurt", ETagStatus.Combat, EPhraseTrigger.OnBeingHurt, EPhraseTrigger.HurtMedium, EPhraseTrigger.HurtLight);
                    Cornered("you shot him up close");
                    return;
                }
                else if (Time.time >= _hurtCooldown || _committed)
                {
                    _hurtCooldown = Time.time + 8f;
                    if (!Voice("hurt", ETagStatus.Combat, EPhraseTrigger.OnBeingHurt, EPhraseTrigger.HurtMedium, EPhraseTrigger.HurtLight, EPhraseTrigger.OnAgony))
                        Audio?.Play3D(Audio.Pick("hiss_"), Chest(Body), Body.Transform.Original, 1f, 5f, 60f);
                    _fleeAfterAttack = State == Mode.Attack || _committed || _fleeAfterAttack;
                    SetState(Mode.Flee, _committed ? "a leg is gone - he breaks off the charge" : "hurt");
                    return;
                }
            }
            if (_dist < 30f && AimedAtHim()) KeepGunFlicker();           // your light on him: it stutters while it's on him
            if (_litByPlayer && _dist < 35f && Time.time >= _scareCooldown && State != Mode.Attack && !_committed && !Berserk && !Enraged && !FullPower)
            {
                _scareCooldown = Time.time + 6f;
                if (!Voice("your flashlight", ETagStatus.Combat, EPhraseTrigger.OnAgony, EPhraseTrigger.OnBeingHurt, EPhraseTrigger.HurtHeavy))
                    Audio?.Play3D(Audio.Pick("shriek_"), Chest(Body), Body.Transform.Original, 1f, 5f, 75f);
                SetState(Mode.Flee, "your flashlight");
                return;
            }
            if (Berserk && _seen && (State == Mode.Stalk || State == Mode.Hide || State == Mode.Sabotage || (State == Mode.Approach && !_committed)))
            {
                Cornered("berserk - he wants you to see him");
                return;
            }
            if (_seen && _dist < SpotChargeDistance && !Enraged && (State == Mode.Stalk || State == Mode.Hide || State == Mode.Sabotage || (State == Mode.Approach && !_committed)))
            {
                Cornered("you found him up close");
                return;
            }
            if (_seen && !Enraged && (State == Mode.Stalk || State == Mode.Hide || State == Mode.Sabotage || (State == Mode.Approach && !_committed)))
            {
                SetState(Mode.Flee, "you looked at him");
            }
        }

        private static void Move(BotOwner b, Vector3 to, bool sprint, bool rush = false)
        {
            if (CrashStep(b)) return;          // crashing: he stumbles
            if (Crashing) sprint = false;
            bool door = AtDoor(b, to);          // a door on his way: open it, no sprinting through
            _atDoorNow = door;
            if (door) sprint = false;
            b.SetPose(1f);
            b.Sprint(sprint);
            Sprinting = sprint;
            bool fresh = (to - _lastGoTo).sqrMagnitude > 1.5f * 1.5f || Time.time - _lastGoToAt > 0.6f || !b.Mover.HasPathAndNoComplete;
            if (fresh)
            {
                _lastGoTo = to;
                _lastGoToAt = Time.time;
                b.Mover.GoToPoint(to, !rush, rush ? 0.6f : 1.2f, false, false);
            }
            if (!door) b.Steering.LookToMovingDirection();
        }

        private static Vector3 _lastGoTo;
        private static float _lastGoToAt;

        private static void Hold(BotOwner b)
        {
            b.Sprint(false);
            b.StopMove();
            b.SetPose(0.55f);
            if (Time.time >= _nextLook)
            {
                _nextLook = Time.time + 0.5f;
                b.Steering.LookToPoint(Chest(_me));
            }
        }

        private static void Relocate(BotOwner b, float minR, float maxR, bool behind, string why)
        {
            // far away or stuck: reappear closer, but only where neither spot can be seen
            if (_seen) return;
            var spot = FindSpot(minR, maxR, behind, needPath: false, tries: 30);
            if (!spot.HasValue) return;
            try
            {
                b.Mover.Teleport(spot.Value);
                _spot = spot;
                Dbg($"slipped through the dark to {(spot.Value - _me.Position).magnitude:0} m behind you ({why})");
            }
            catch (Exception)
            {
                // try again later
            }
        }

        private static void DoHide(BotOwner b)
        {
            KnifeOut(b);
            if (!_spot.HasValue && Time.time >= _nextSpot)
            {
                _nextSpot = Time.time + 1.5f;
                _spot = FindSpot(22f, 40f, behind: true, needPath: true);
            }
            if (_spot.HasValue && (Body.Position - _spot.Value).magnitude > 2f) Move(b, _spot.Value, true);
            else Hold(b);
            if (Time.time - _stateSince > _hideFor) SetState(Mode.Stalk, "done hiding");
        }

        private static float _nextStalkLog;

        private static void DoStalk(BotOwner b)
        {
            if (DebugLog() && Time.time >= _nextStalkLog)
            {
                _nextStalkLog = Time.time + 15f;
                string why = Time.time < _approachAfter ? $"waits {(_approachAfter - Time.time):0} s" : _seen ? "you see him"
                    : (DarkMatters && _playerInLight) ? "you're in the light" : _dist >= 40f ? "too far" : "ready";
                Dbg($"stalking: {_dist:0} m from you, spot {(_spot.HasValue ? $"{(_spot.Value - _me.Position).magnitude:0} m from you" : "none found")}, rush: {why}, power {BlackoutLink.Power}, your lights on: {PlayerLights.Count}{(_litByPlayer ? " (on him)" : "")}, {HighStatus()}");
            }
            BloodHurries();
            float want = Shadowing ? 30f : _marked ? 12f : 16f;     // after being driven off: shadows you from further off
            // keep a spot behind the player, re-pick it as the player moves or turns
            bool spotBad = !_spot.HasValue || (_spot.Value - _me.Position).magnitude > want + 18f ||
                           !Physics.Linecast(_eye, _spot.Value + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask);
            if (spotBad && Time.time >= _nextSpot)
            {
                _nextSpot = Time.time + 2f;
                _spot = FindSpot(want, want + 12f, behind: true, needPath: true) ?? _spot;
            }
            if (_dist > 45f)
            {
                if (_farSince < 0f) _farSince = Time.time;
                if (Time.time - _farSince > 12f)
                {
                    _farSince = -1f;
                    Relocate(b, 28f, 40f, true, "you got too far ahead");
                }
            }
            else
            {
                _farSince = -1f;
            }
            if (_spot.HasValue && (Body.Position - _spot.Value).magnitude > 2f) Move(b, _spot.Value, (Body.Position - _spot.Value).magnitude > 5f);
            else Hold(b);

            if (Time.time >= _nextLaugh && _dist > 8f && _dist < 30f)
            {
                _nextLaugh = Time.time + (_marked ? Rnd(14f, 30f) : Rnd(22f, 48f)) * (High ? 0.5f : 1f);   // high: he can't stop laughing
                Laugh(_dist < 16f);
            }

            bool ready = Time.time >= _approachAfter && !_seen && !(DarkMatters && _playerInLight) && _dist < 40f;
            // the light keeps him off you for a while, not forever: past his patience he comes anyway
            bool onlyLight = Time.time >= _approachAfter && !_seen && _dist < 40f && DarkMatters && _playerInLight;
            if (!onlyLight) _lightWaitSince = -1f;
            else if (_lightWaitSince < 0f) _lightWaitSince = Time.time;
            bool impatient = onlyLight && Time.time - _lightWaitSince > LightPatience;
            if (impatient) ready = true;
            if (ready && Crashing) ready = false;                                          // no charges while he's crashing
            if (MaybeSabotage()) return;                                                   // the lights are on: he goes to cut them
            if (ready && PreChargeDose(b)) return;                                         // a high first, then the charge
            if (ready)
            {
                SetState(Mode.Approach, impatient ? $"done waiting - {LightPatience:0} s in the light is all you get" : Berserk ? "berserk" : _marked ? "you carry Admin's key" : "his moment");
                _ignoreLight = impatient;
                _lightWaitSince = -1f;
            }
            KnifeOut(b);
        }

        private static void KnifeOut(BotOwner b)
        {
            try
            {
                var wm = b.WeaponManager;
                if (wm != null && !wm.IsMelee && wm.Selector.CanChangeToMeleeWeapons) wm.Selector.ChangeToMelee();
            }
            catch (Exception)
            {
                // try again next frame
            }
        }

        /// <summary>
        /// One of his own (Cultist) voice lines: the first of the triggers his voice has a line for.
        /// False = none found (or turned off) - the caller plays the built-in sound instead.
        /// </summary>
        private static bool Voice(string what, ETagStatus mood, params EPhraseTrigger[] triggers)
        {
            if (!CultistVoice() || Body?.Speaker == null) return false;
            ETagStatus health = ETagStatus.Healthy;
            try { health = Body.HealthStatus; } catch (Exception) { }
            var tagSets = new[] { mood | health, mood, ETagStatus.Unaware | health, ETagStatus.Aware | health, ETagStatus.Combat | health };
            foreach (var t in triggers)
            {
                foreach (var tags in tagSets)
                {
                    TagBank bank = null;
                    StalkerPatches.AllowVoice = true;
                    try
                    {
                        bank = Body.Speaker.Play(t, tags, true);
                    }
                    catch (Exception)
                    {
                        bank = null;
                    }
                    finally
                    {
                        StalkerPatches.AllowVoice = false;
                    }
                    if (bank != null)
                    {
                        Dbg($"says a Cultist line: {t} [{tags}] ({what}, {_dist:0} m away)");
                        return true;
                    }
                }
            }
            Dbg($"no Cultist line for {what} - built-in sound instead");
            return false;
        }

        private static void Laugh(bool near, bool afterHit = false)
        {
            if (Body == null || Audio == null) return;
            if (afterHit ? Voice("after the hit", ETagStatus.Combat, EPhraseTrigger.KnifeKill, EPhraseTrigger.EnemyHit, EPhraseTrigger.OnEnemyShot, EPhraseTrigger.OnFight)
                     : Voice("stalking", ETagStatus.Unaware, EPhraseTrigger.OnMutter, EPhraseTrigger.OnEnemyConversation, EPhraseTrigger.Provocation,
                         EPhraseTrigger.OnRepeatedContact, EPhraseTrigger.OnLostVisual, EPhraseTrigger.OnFirstContact))
                return;
            string clip = near ? Audio.Pick("laugh_near_") : Audio.Pick("laugh_");
            Audio.Play3D(clip, Chest(Body) + Vector3.up * 0.4f, Body.Transform.Original, 0.80f, near ? 5f : 8f, near ? 40f : 60f);
            Dbg($"laughs ({_dist:0} m away)");
        }

        private static void DoApproach(BotOwner b)
        {
            if (!_committed && !Enraged && !_ignoreLight && DarkMatters && _playerInLight && _dist > 6f && Time.time - _stateSince < 1.5f)
            {
                SetState(Mode.Stalk, "you stepped into the light");
                return;
            }
            Move(b, _me.Position, true, rush: true);
            KnifeOut(b);
            var here = Body.Position;
            _chargeDist += new Vector3(here.x - _chargeLast.x, 0f, here.z - _chargeLast.z).magnitude;
            _chargeLast = here;
            _chargeFrames++;
            if (Body.IsSprintEnabled) _chargeSprintFrames++;
            if (_atDoorNow) _chargeDoorTime += Time.deltaTime;
            if (_atDoorNow)
            {
                // opening a door on the way doesn't count against the charge
                _stateSince += Time.deltaTime;
                _commitAt += Time.deltaTime;
            }
            if (!_committed && _dist < CommitDistance())
            {
                _committed = true;
                _commitAt = Time.time;
                Dbg($"commits to the attack at {_dist:0.0} m - no turning back");
            }
            if (!_whispered && (_committed || _dist < 8f))
            {
                _whispered = true;
                if (!Voice("before the strike", ETagStatus.Combat, EPhraseTrigger.OnFight, EPhraseTrigger.OnFirstContact, EPhraseTrigger.Provocation, EPhraseTrigger.OnMutter))
                    Audio?.Play3D(Audio.Pick("whisper_"), Chest(Body) + Vector3.up * 0.4f, Body.Transform.Original, 1f, 4f, 20f);
            }
            if (_dist < 2.8f)
            {
                bool wasCommitted = _committed;
                SetState(Mode.Attack, "in reach");
                _committed = wasCommitted;
                _hitLanded = false;
                _swings = 0;
                _nextSwing = Time.time + 0.15f;
                try { if (b.WeaponManager?.Melee != null) b.WeaponManager.Melee.ShallEndRun = false; } catch (Exception) { }
            }
            else if (Time.time - _stateSince > 25f || (_committed && Time.time - _commitAt > 6f))
            {
                SetState(Mode.Stalk, "couldn't get close");
            }
        }

        private static float _commitAt, _lightWaitSince = -1f;
        private static bool _ignoreLight;
        /// <summary>How long you can stay in the light before he comes anyway (shorter at full power).</summary>
        private static float LightPatience => FullPower ? 20f : 35f;

        private static void LogChargeSpeed()
        {
            float t = Time.time - _chargeStart;
            if (t < 0.5f || _chargeFrames == 0) return;
            int n = Mathf.Max(1, ChargeSteps);
            Dbg($"charge: {_chargeDist:0.0} m in {t:0.0} s = {_chargeDist / t:0.0} m/s (the game let him sprint {100f * _chargeSprintFrames / _chargeFrames:0}% of the way, boosted {100f * ChargeBoosted / n:0}% of his steps"
                + $" | not running {100f * ChargeNoRun / n:0}%, standing {100f * ChargeStill / n:0}%, near an edge {100f * ChargeClipped / n:0}%, game pace {ChargeBase / Mathf.Max(1, n - ChargeNoRun - ChargeStill):0.0} m/s, doors {_chargeDoorTime:0.0} s)");
        }

        private static float _limpCheck;
        private static bool _limping;
        /// <summary>A leg is destroyed (checked a few times a second): no charge speed then.</summary>
        public static bool Limping
        {
            get
            {
                if (Time.time >= _limpCheck)
                {
                    _limpCheck = Time.time + 0.25f;
                    _limping = Body != null && LegGone();
                }
                return _limping;
            }
        }

        private static bool LegGone()
        {
            try
            {
                var hc = Body.HealthController;
                return hc.IsBodyPartDestroyed(EBodyPart.LeftLeg) || hc.IsBodyPartDestroyed(EBodyPart.RightLeg);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static float _nextSwing, _hitAt;
        private static int _swings;

        private static void DoAttack(BotOwner b)
        {
            // he drives the charge himself: the game's own melee run stalls when the bot can't "see" you in the dark
            try
            {
                b.BotsGroup.AddEnemy(_me, EBotEnemyCause.initial);
                if (b.EnemiesController.EnemyInfos.TryGetValue(_me, out var info) && b.Memory.GoalEnemy != info) b.Memory.GoalEnemy = info;
                var wm = b.WeaponManager;
                var melee = wm.Melee;
                if (!wm.IsMelee && wm.Selector.CanChangeToMeleeWeapons) wm.Selector.ChangeToMelee();
                if (_dist > 1.0f) Move(b, _me.Position, true, rush: true);      // right up to you: a swing from 2 m+ only cuts air
                else
                {
                    b.Sprint(false);
                    b.StopMove();
                }
                if (!_atDoorNow) b.Steering.LookToPoint(Chest(_me));
                if (_dist < 2.0f && melee?.KnifeController != null && wm.IsMelee && Time.time >= _nextSwing && !_hitLanded)
                {
                    _nextSwing = Time.time + 0.9f;
                    _swings++;
                    melee.KnifeController.MakeKnifeKick();
                }
            }
            catch (Exception ex)
            {
                Dbg($"attack error: {ex.Message}");
                Move(b, _me.Position, true, rush: true);
            }
            float t = Time.time - _stateSince;
            if (_hitLanded && Time.time - _hitAt > 0.5f) Leave("hit and run");
            else if (t > 8f) Leave($"missed you ({_swings} swing(s))");
            else if (_dist > 12f && t > 3f) Leave($"you got away ({_swings} swing(s))");
        }

        private static void Leave(string why)
        {
            _fleeAfterAttack = true;
            SetState(Mode.Flee, why);
        }

        /// <summary>Found or shot up close: no running away now - he goes for you.</summary>
        private static void Cornered(string why)
        {
            if (Crashing)
            {
                SetState(Mode.Flee, why + " - but he's crashing, he stumbles away");
                return;
            }
            SetState(Mode.Approach, why);
            _unseenCharge = false;                   // you found him: no backstab
            _committed = true;
            _commitAt = Time.time;
            _approachAfter = Time.time;
            Dbg($"cornered at {_dist:0.0} m - he goes for you");
        }

        private static Vector3 _fleeCheckPos;
        private static float _nextFleeCheck;

        /// <summary>Any reachable point roughly away from you (walls block straight lines, so try several ways).</summary>
        private static Vector3? EscapeRoute()
        {
            Vector3 away = Body.Position - _me.Position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -_me.Transform.forward;
            away.Normalize();
            var path = new NavMeshPath();
            foreach (float dist in new[] { 22f, 14f, 8f })
            {
                foreach (float angle in new[] { 0f, 35f, -35f, 70f, -70f, 110f, -110f })
                {
                    Vector3 cand = Body.Position + Quaternion.Euler(0f, angle, 0f) * away * dist;
                    if (!NavMesh.SamplePosition(cand, out var hit, 4f, NavMesh.AllAreas)) continue;
                    if (NavMesh.CalculatePath(Body.Position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                        return hit.position;
                }
            }
            return null;
        }

        private static void DoFlee(BotOwner b)
        {
            KnifeOut(b);
            if (_fleeCheckAt > 0f && Time.time >= _fleeCheckAt)
            {
                _fleeCheckAt = -1f;
                string legs = "";
                try
                {
                    var hc = Body.HealthController;
                    legs = $", legs {hc.GetBodyPartHealth(EBodyPart.LeftLeg).Current:0}/{hc.GetBodyPartHealth(EBodyPart.RightLeg).Current:0} HP";
                }
                catch (Exception)
                {
                    // logging only
                }
                Dbg($"running away: {(Body.Position - _fleeFrom).magnitude:0.0} m in the first second, sprinting {(Body.IsSprintEnabled ? "yes" : "not yet")}{legs}");
            }
            if (!_spot.HasValue && Time.time >= _nextSpot)
            {
                _nextSpot = Time.time + 1f;
                _spot = FindSpot(28f, 45f, behind: false, needPath: true, tries: 30, awayFromPlayer: true) ?? EscapeRoute();
                if (!_spot.HasValue) Dbg("no escape route found - trying again");
                _fleeCheckPos = Body.Position;
                _nextFleeCheck = Time.time + 3f;
            }
            if (_spot.HasValue) Move(b, _spot.Value, true);
            // not getting anywhere (blocked, or his legs are gone): pick another way out
            if (Time.time >= _nextFleeCheck)
            {
                _nextFleeCheck = Time.time + 3f;
                float moved = (Body.Position - _fleeCheckPos).magnitude;
                _fleeCheckPos = Body.Position;
                if (moved < 1f && _spot.HasValue && (Body.Position - _spot.Value).magnitude > 2.5f)
                {
                    string legs = "?";
                    try
                    {
                        var hc = Body.HealthController;
                        legs = $"legs {hc.GetBodyPartHealth(EBodyPart.LeftLeg).Current:0}/{hc.GetBodyPartHealth(EBodyPart.RightLeg).Current:0} HP";
                    }
                    catch (Exception)
                    {
                        // logging only
                    }
                    Dbg($"stuck while fleeing (moved {moved:0.0} m in 3 s, {legs}) - new way out");
                    _spot = EscapeRoute();
                }
            }
            bool there = _spot.HasValue && (Body.Position - _spot.Value).magnitude < 2.5f;
            if ((there && !_seen) || Time.time - _stateSince > 14f)
            {
                if (_enragePending && !LegGone())
                {
                    _enragePending = false;
                    _fleeAfterAttack = false;
                    _enragedUntil = Time.time + 20f;
                    SetState(Mode.Approach, "enraged - you kept driving him off in the light");
                    Voice("enraged", ETagStatus.Combat, EPhraseTrigger.OnFight, EPhraseTrigger.Provocation, EPhraseTrigger.OnFirstContact);
                    return;
                }
                if (MaybeRetreat())
                {
                    _fleeAfterAttack = false;
                    _ignoreSightUntil = Time.time + 2.5f;
                    return;
                }
                SetState(Mode.Hide, "got away");
                _approachAfter = _fleeAfterAttack ? Time.time + NextChargeDelay() : Mathf.Max(_approachAfter, Time.time + Rnd(6f, 12f));
                if (FullPower) _approachAfter = Time.time + (_approachAfter - Time.time) * 0.6f;     // lit facility: he doesn't wait as long
                _fleeAfterAttack = false;
                _ignoreSightUntil = Time.time + 2.5f;      // settle in before he spooks again
            }
        }

        private static void DoLeave(BotOwner b)
        {
            if (!_spot.HasValue && Time.time >= _nextSpot)
            {
                _nextSpot = Time.time + 2f;
                _spot = FindSpot(45f, 70f, behind: false, needPath: true, tries: 30, awayFromPlayer: true);
            }
            if (_spot.HasValue) Move(b, _spot.Value, true);
            else Hold(b);
            if (!_seen && _dist > 35f)
            {
                if (_hiddenSince < 0f) _hiddenSince = Time.time;
            }
            else
            {
                _hiddenSince = -1f;
            }
            if ((_hiddenSince > 0f && Time.time - _hiddenSince > 4f) || (Time.time - _stateSince > 60f && !_seen))
            {
                Log.LogInfo("[Stalker] he's gone (Killa's here).");
                if (!Voice("leaving", ETagStatus.Unaware, EPhraseTrigger.OnMutter, EPhraseTrigger.OnEnemyConversation))
                    Audio?.Play3D(Audio.Pick("laugh_"), Chest(Body) + Vector3.up * 0.4f, null, 1f, 4f, 65f);
                try
                {
                    var bot = Bot;
                    SetState(Mode.Gone, "slipping away");
                    Later(1.5f, () =>
                    {
                        try { bot?.LeaveData?.RemoveFromMap(); }
                        catch (Exception ex) { Log.LogWarning($"[Stalker] despawn failed: {ex.Message}"); }
                        if (Bot == bot)
                        {
                            State = Mode.None;
                            Bot = null;
                            Body = null;
                        }
                    });
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }

        // ---------------------------------------------------------------- his knife on you

        private static void OnMeHit(DamageInfo dmg, EBodyPart part, float val)
        {
            try
            {
                if (_profileId == null || dmg.Player?.iPlayer?.ProfileId != _profileId) return;
                _hitLanded = true;
                _hitAt = Time.time;
                var me = Singleton<GameWorld>.Instance?.MainPlayer;
                var hc = me?.ActiveHealthController;
                if (hc == null || !hc.IsAlive) return;
                _cuts++;
                bool first = _cuts == 1;
                if (first) _firstCutAt = Time.time;
                bool backstab = first && Backstab() && _unseenCharge;
                Log.LogInfo($"[Stalker] cut you ({part}, {dmg.Damage:0} damage){(backstab ? " - BACKSTAB: you never saw him coming, the same again to your chest" : "")}{(first ? "" : $" - cut {_cuts} while you're blind")}.");
                if (backstab)
                {
                    var extra = dmg;
                    hc.ApplyDamage(EBodyPart.Chest, dmg.Damage, extra);
                }
                bool deep = DeepCuts;
                if (deep)
                {
                    var extra = dmg;
                    hc.ApplyDamage(part, dmg.Damage * 0.5f, extra);
                    Log.LogInfo($"[Stalker] PNB: a deeper cut (+{dmg.Damage * 0.5f:0} to your {part}, one more bleed).");
                }

                // heavy bleeds: the first cut opens several (where he cut, then other limbs), each cut after it one more
                int n = (first ? Mathf.Clamp(Bleeds(), 0, 7) : (Bleeds() > 0 ? 1 : 0)) + (deep ? 1 : 0);
                var parts = new List<EBodyPart> { part };
                var others = new List<EBodyPart> { EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg }
                    .Where(p => p != part).OrderBy(_ => Rng.Next()).ToList();
                parts.AddRange(others);
                int done = 0;
                foreach (var p in parts)
                {
                    if (done >= n) break;
                    if (p == EBodyPart.Common || p == EBodyPart.Head) continue;
                    hc.DoBleed(true, p);
                    done++;
                }
                if (first) _blindThisAttack = Rng.Next(100) < BlindChance();
                float blind = _blindThisAttack ? BlindSeconds() : 0f;
                if (first)
                {
                    if (Poison()) hc.DoIntoxication();
                    if (blind <= 0.1f) Dbg("no white-out this time");
                    if (blind > 0.1f)
                    {
                        hc.DoBurnEyes(Body != null ? Chest(Body) : me.Position + me.Transform.forward, 1f, 1f, blind);
                        hc.DoContusion(blind + 1f, 0.6f);
                    }
                    Audio?.Play2D(Audio.Pick("sting_"), 1f);
                }
                // while you're still whited out he stays on you for another cut
                bool more = _cuts < Mathf.Clamp(CutsPerAttack(), 1, 3) && blind > 0.1f && Time.time - _firstCutAt < blind - 0.3f
                            && (State == Mode.Attack || State == Mode.Approach);
                if (more)
                {
                    _hitLanded = false;
                    _nextSwing = Time.time + 0.55f;
                    _stateSince = Time.time;                 // a fresh attack window for the next cut
                    Dbg($"stays on you while you're blind (cut {_cuts} of {CutsPerAttack()})");
                    return;
                }
                Later(1.4f, () => Laugh(true, afterHit: true));
                if (State == Mode.Attack || State == Mode.Approach)
                {
                    _fleeAfterAttack = true;
                    SetState(Mode.Flee, _cuts > 1 ? $"{_cuts} cuts and gone" : "hit and run");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Stalker] hit effects failed: {ex.Message}");
            }
        }
    }
}
