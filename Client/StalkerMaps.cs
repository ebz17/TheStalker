using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace TheStalker
{
    /// <summary>
    /// Where he comes: Labs as always; the other maps only in night raids (21:00-05:00 game time),
    /// the Labyrinth any time (it's underground). Off Labs he wakes 8-20 min into the raid.
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<bool> OtherMaps = () => true;

        private static bool _mapChecked, _mapOk, _onLabs;
        private static float _wakeAfter;
        private static string _mapName;

        private static bool Underground(string loc) =>
            string.Equals(loc, "laboratory", StringComparison.OrdinalIgnoreCase) || string.Equals(loc, "labyrinth", StringComparison.OrdinalIgnoreCase);

        private static string MapName(string loc)
        {
            switch ((loc ?? "").ToLowerInvariant())
            {
                case "laboratory": return "Labs";
                case "labyrinth": return "the Labyrinth";
                case "bigmap": return "Customs";
                case "factory4_day": case "factory4_night": return "Factory";
                case "interchange": return "Interchange";
                case "lighthouse": return "Lighthouse";
                case "rezervbase": return "Reserve";
                case "shoreline": return "Shoreline";
                case "tarkovstreets": return "Streets";
                case "woods": return "Woods";
                case "sandbox": case "sandbox_high": return "Ground Zero";
                default: return loc;
            }
        }

        /// <summary>Once per raid: can he come on this map, at this time of day?</summary>
        private static bool MapAllowed(GameWorld gw)
        {
            if (_mapChecked) return _mapOk;
            _mapChecked = true;
            string loc = gw.LocationId;
            _mapName = MapName(loc);
            _onLabs = string.Equals(loc, "laboratory", StringComparison.OrdinalIgnoreCase);
            if (_onLabs) return _mapOk = true;
            if (string.Equals(loc, "hideout", StringComparison.OrdinalIgnoreCase)) return _mapOk = false;
            if (!OtherMaps())
            {
                Log.LogInfo($"[Stalker] not on {_mapName} (setting \"Other maps (night raids)\" is off).");
                return _mapOk = false;
            }
            _wakeAfter = Rnd(8f, 20f) * 60f;
            if (Underground(loc) || string.Equals(loc, "factory4_night", StringComparison.OrdinalIgnoreCase)) return _mapOk = true;
            int hour = -1;
            try { hour = gw.GameDateTime.Calculate().Hour; }
            catch (Exception) { }
            bool night = hour >= 21 || (hour >= 0 && hour < 5);
            if (!night) Log.LogInfo($"[Stalker] not on {_mapName} this raid: it's daytime ({(hour < 0 ? "?" : hour.ToString("00"))}:00) - he only comes at night.");
            return _mapOk = night;
        }

        private static void ResetMaps()
        {
            _mapChecked = _mapOk = _onLabs = false;
            _wakeAfter = 0f;
            _mapName = null;
        }
    }
}
