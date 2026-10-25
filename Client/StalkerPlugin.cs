using System;
using System.Collections;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TheStalker
{
    [BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.morebotsapi.tacticaltoaster", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.blackoutaddons.powerrestore", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("com.blackoutaddons.thestalker", "The Stalker", "1.0.43")]
    public class StalkerPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _enabled, _otherMaps, _poison, _flashlight, _leaveOnKilla, _doors, _sabotage, _debug;
        private static ConfigEntry<int> _stims;
        private static ConfigEntry<float> _commit;
        private static ConfigEntry<bool> _lights, _gunFlicker, _backstab, _blood;
        private static ConfigEntry<int> _cutsPer;
        private ConfigEntry<int> _chance, _bleeds, _blindChance;
        private ConfigEntry<float> _wake, _standalone, _blind, _speed, _chargeSpeed, _volume;
        private ConfigEntry<string> _paLang, _voice;
        private ConfigEntry<KeyboardShortcut> _testKey;
        private bool _ok;

        private void Awake()
        {
            Stalker.Log = Logger;
            const string g = "1. General", d = "2. Danger", s = "3. Sound", t = "4. Testing";
            _enabled = Config.Bind(g, "Enabled", true, "The Stalker can appear (Labs, and the other maps if \"Other maps (night raids)\" is on).");
            _otherMaps = Config.Bind(g, "Other maps (night raids)", true, "He can also come on every other map in night raids (21:00-05:00), and in the Labyrinth any time. Same chance; he wakes 8-20 min in.");
            _chance = Config.Bind(g, "Chance (%)", 35, new ConfigDescription("Chance he's in a raid at all (Labs, and night raids on the other maps).", new AcceptableValueRange<int>(0, 100)));
            _wake = Config.Bind(g, "Wakes after minutes in the dark", 10f, new ConfigDescription(
                "With Blackout Addons: he wakes when the generator control burns out, or after this long with the lights out.", new AcceptableValueRange<float>(1f, 40f)));
            _standalone = Config.Bind(g, "Wakes after minutes (without Blackout Addons)", 12f, new ConfigDescription(
                "Without Blackout Addons there's no blackout to wait for: he wakes this long into the raid.", new AcceptableValueRange<float>(1f, 40f)));
            // new name on purpose: the old "Leaves when Killa breaks in" (on) stays behind unused, so everyone gets the new default
            _leaveOnKilla = Config.Bind(g, "Slips away when Killa breaks in", false, "Off: he stays in the raid with Killa and Tagilla (they're enemies - Killa can kill him). On: he leaves when Killa arrives.");
            _doors = Config.Bind(g, "Opens doors", true, "He opens shut doors on his way instead of walking past them.");
            _sabotage = Config.Bind(d, "Cuts the power", true, "With Blackout Addons 1.27.10+: when the power is back on he may sneak to the generator switch and shut it down (up to twice a raid). Guard the switch to stop him.");
            _bleeds = Config.Bind(d, "Heavy bleeds per hit", 3, new ConfigDescription("Heavy bleeds his knife opens (where he cut you, then other limbs).", new AcceptableValueRange<int>(0, 6)));
            _poison = Config.Bind(d, "Poison", true, "His knife poisons you.");
            _blind = Config.Bind(d, "Blind seconds", 4f, new ConfigDescription("White-out like a flashbang after his hit. 0 = off.", new AcceptableValueRange<float>(0f, 10f)));
            _blindChance = Config.Bind(d, "Blind chance (%)", 50, new ConfigDescription("Chance his first cut whites you out. Without the white-out there's no second cut either - he hits and runs.", new AcceptableValueRange<int>(0, 100)));
            _speed = Config.Bind(d, "Sprint speed", 1.35f, new ConfigDescription("How much faster than a normal sprint while he moves around.", new AcceptableValueRange<float>(1f, 2f)));
            _chargeSpeed = Config.Bind(d, "Charge speed (m/s)", 7.5f, new ConfigDescription("How fast he charges at you and bolts away. You sprint at roughly 6-7 m/s.", new AcceptableValueRange<float>(5f, 10f)));
            _flashlight = Config.Bind(d, "Flashlight scares him", true, "Shine a light on him and he screams and runs.");
            _commit = Config.Bind(d, "Commits to the attack at (m)", 8f, new ConfigDescription("Once he is this close in a charge he keeps coming: looking at him, your flashlight or shooting him will not turn him back (destroying a leg will). Find him or shoot him this close and he charges instead of running.", new AcceptableValueRange<float>(3f, 15f)));
            _lights = Config.Bind(d, "Lights die around him", true, "With Blackout Addons 1.27.5+: the emergency lamps near him go out, and a little further away they flicker and die as he passes.");
            _gunFlicker = Config.Bind(d, "Your light stutters on him", true, "Point your weapon light or headlamp at him (within 30 m) and it flickers the whole time it's on him.");
            _backstab = Config.Bind(d, "Backstab", true, "A charge you never saw coming (not seen, not lit by your light) hits twice as hard: the same damage again to your chest.");
            _cutsPer = Config.Bind(d, "Cuts per attack", 2, new ConfigDescription("While you're whited out he stays on you for more cuts (each after the first opens one more heavy bleed). 1 = hit and run.", new AcceptableValueRange<int>(1, 3)));
            _blood = Config.Bind(d, "Smells blood", true, "Bleeding or below half health: he comes back after 10-20 s instead of 30-60 s.");
            _stims = Config.Bind(d, "Stims he uses", 6, new ConfigDescription("How many of his stims he injects per raid: healing ones when he's badly hurt (Zagustin, eTG-c, Propital, Perfotoran, Trimadol) and highs on purpose (Obdolbos 2 berserk then a crash, Adrenaline lunge, PNB deeper cuts, SJ6 faster bolts). 0 = never. How many he carries is set in the server mod's config.jsonc.", new AcceptableValueRange<int>(0, 9)));
            _volume = Config.Bind(s, "Volume", 1f, new ConfigDescription("His laughs, whispers and screams.", new AcceptableValueRange<float>(0f, 1f)));
            _voice = Config.Bind(s, "His voice", "Cultist voice", new ConfigDescription(
                "Cultist voice = the game's own Cultist lines (mutters while he stalks, his fight line before he strikes, agony, pain). " +
                "Built-in sounds = the mod's own laughs, whispers and screams.", new AcceptableValueList<string>("Cultist voice", "Built-in sounds")));
            _paLang = Config.Bind(s, "PA language", "Russian", new ConfigDescription("Language of the PA warning when he appears.", new AcceptableValueList<string>("Russian", "English")));
            _testKey = Config.Bind(t, "Wake him now", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl), "Testing: wakes him right away in any raid (ignores the chance, the map and the time of day).");
            _debug = Config.Bind(t, "Debug log", true, "Log what he's doing (state changes, laughs, hits).");

            Stalker.Enabled = () => _enabled.Value && _ok;
            Stalker.Chance = () => _chance.Value;
            Stalker.WakeMinutes = () => _wake.Value;
            Stalker.StandaloneMinutes = () => _standalone.Value;
            Stalker.LeaveOnKilla = () => _leaveOnKilla.Value;
            Stalker.OpensDoors = () => _doors.Value;
            Stalker.OtherMaps = () => _otherMaps.Value;
            Stalker.CutsPower = () => _sabotage.Value;
            Stalker.Bleeds = () => _bleeds.Value;
            Stalker.Poison = () => _poison.Value;
            Stalker.BlindSeconds = () => _blind.Value;
            Stalker.BlindChance = () => _blindChance.Value;
            Stalker.SpeedMult = () => _speed.Value;
            Stalker.ChargeSpeed = () => _chargeSpeed.Value;
            Stalker.FlashlightScares = () => _flashlight.Value;
            Stalker.Russian = () => _paLang.Value != "English";
            Stalker.DebugLog = () => _debug.Value;
            Stalker.StimUses = () => _stims.Value;
            Stalker.CommitDistance = () => _commit.Value;
            Stalker.KillsLights = () => _lights.Value;
            Stalker.GunLightFlicker = () => _gunFlicker.Value;
            Stalker.Backstab = () => _backstab.Value;
            Stalker.CutsPerAttack = () => _cutsPer.Value;
            Stalker.SmellsBlood = () => _blood.Value;
            Stalker.CultistVoice = () => _voice.Value != "Built-in sounds";
            Stalker.Later = (delay, action) => StartCoroutine(After(delay, action));

            var harmony = new Harmony("com.blackoutaddons.thestalker");
            try
            {
                _ok = StalkerSpawn.Install(harmony, Logger) && StalkerLayerSetup.Install(Logger);
                StalkerPatches.Install(harmony);
            }
            catch (Exception ex)
            {
                _ok = false;
                Logger.LogError($"[Stalker] setup failed: {ex}");
            }

            Stalker.Audio = new StalkerAudio(Logger);
            _volume.SettingChanged += (_, __) => Stalker.Audio.Volume = _volume.Value;
            Stalker.Audio.Volume = _volume.Value;
            string folder = Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", "sounds");
            StartCoroutine(Stalker.Audio.LoadFolder(folder));
            Logger.LogInfo($"[Stalker] The Stalker {Info.Metadata.Version} loaded{(_ok ? "" : " - but turned OFF, see the errors above")}. Blackout Addons {(BlackoutLink.Present ? "found" : "not found (he wakes on a timer)")}.");
        }

        private static IEnumerator After(float delay, Action action)
        {
            yield return new WaitForSeconds(delay);
            try { action(); }
            catch (Exception) { /* raid ended */ }
        }

        private void Update()
        {
            try
            {
                Stalker.Tick();
                if (_ok && _testKey.Value.IsDown()) Stalker.WakeNow();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Stalker] {ex.Message}");
            }
        }
    }
}
