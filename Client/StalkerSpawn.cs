using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using EFT;
using HarmonyLib;

namespace TheStalker
{
    /// <summary>
    /// The server puts his wave in Labs at raid start (Time -1). This keeps it back until he wakes up, then sends it.
    /// </summary>
    internal static class StalkerSpawn
    {
        public const string BossName = "bossStalker";
        private static ManualLogSource _log;
        private static MethodInfo _spawnMethod;
        private static BotBossSpawn _spawner;
        private static BossLocationSpawn _wave;
        private static object _params;
        private static bool _releasing;
        public static bool Held => _wave != null;
        public static bool Released { get; private set; }

        public static bool Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            _spawnMethod = AccessTools.GetDeclaredMethods(typeof(BotBossSpawn)).FirstOrDefault(m =>
                m.Name == "Spawn" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(BossLocationSpawn));
            if (_spawnMethod == null)
            {
                log.LogError("[Stalker] boss spawn method not found - he can't be held back, so he's turned off.");
                return false;
            }
            harmony.Patch(_spawnMethod, prefix: new HarmonyMethod(typeof(StalkerSpawn), nameof(SpawnPrefix)) { priority = Priority.First });
            return true;
        }

        public static bool IsStalkerWave(BossLocationSpawn wave) =>
            wave != null && string.Equals(wave.BossName, BossName, StringComparison.OrdinalIgnoreCase);

        private static bool SpawnPrefix(BotBossSpawn __instance, BossLocationSpawn wave, object[] __args, ref Task __result)
        {
            try
            {
                if (_releasing || Released || !IsStalkerWave(wave)) return true;   // after release: let the game's own retries through
                if (_wave == null && !Released)
                {
                    _spawner = __instance;
                    _wave = wave;
                    _params = __args.Length > 1 ? __args[1] : null;
                    _log.LogInfo("[Stalker] wave held at raid start - he waits in the dark.");
                }
                __result = Task.CompletedTask;
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Stalker] hold failed ({ex.Message}).");
                __result = Task.CompletedTask;
                return false;
            }
        }

        public static bool Release(string why)
        {
            if (_wave == null || Released) return false;
            var spawner = _spawner;
            var wave = _wave;
            var prms = _params;
            _releasing = true;
            try
            {
                var args = new object[_spawnMethod.GetParameters().Length];
                args[0] = wave;
                if (args.Length > 1) args[1] = prms;
                var task = _spawnMethod.Invoke(spawner, args) as Task;
                _spawner = null;
                _wave = null;
                _params = null;
                Released = true;
                task?.ContinueWith(t => _log.LogWarning($"[Stalker] spawn error: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                _log.LogInfo($"[Stalker] he wakes up ({why}).");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Stalker] release failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            finally
            {
                _releasing = false;
            }
        }

        public static void ResetRaid()
        {
            _spawner = null;
            _wave = null;
            _params = null;
            Released = false;
            _releasing = false;
        }
    }
}
