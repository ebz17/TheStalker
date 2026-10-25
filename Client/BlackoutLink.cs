using System;
using System.Collections.Generic;
using System.Reflection;

namespace TheStalker
{
    /// <summary>
    /// Reads Blackout Addons' public state (BlackoutPowerRestore.BlackoutApi) by reflection, so The Stalker works with or
    /// without Blackout Addons installed.
    /// </summary>
    internal static class BlackoutLink
    {
        private static Type _api;
        private static bool _looked;
        private static readonly Dictionary<string, PropertyInfo> Props = new Dictionary<string, PropertyInfo>();

        private static Type Api
        {
            get
            {
                if (_looked) return _api;
                _looked = true;
                try
                {
                    _api = Type.GetType("BlackoutPowerRestore.BlackoutApi, BlackoutPowerRestore", false);
                }
                catch (Exception)
                {
                    _api = null;
                }
                return _api;
            }
        }

        public static bool Present => Api != null;

        private static T Get<T>(string name, T fallback)
        {
            var api = Api;
            if (api == null) return fallback;
            try
            {
                if (!Props.TryGetValue(name, out var p))
                {
                    p = api.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                    Props[name] = p;
                }
                return p == null ? fallback : (T)p.GetValue(null);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static PropertyInfo _presence;
        private static bool _presenceLooked;

        /// <summary>(Blackout Addons 1.27.5+) Tell it where he is: the emergency lamps around him die. False = not supported.</summary>
        public static bool SetPresence(UnityEngine.Vector3? pos)
        {
            var api = Api;
            if (api == null) return false;
            try
            {
                if (!_presenceLooked)
                {
                    _presenceLooked = true;
                    _presence = api.GetProperty("DarkPresence", BindingFlags.Public | BindingFlags.Static);
                }
                if (_presence == null || !_presence.CanWrite) return false;
                _presence.SetValue(null, pos);
                return true;
            }
            catch (Exception)
            {
                _presence = null;
                return false;
            }
        }

        private static MethodInfo _requestCut;
        private static bool _cutLooked;

        /// <summary>(Blackout Addons 1.27.10+) Shut the generators down from the switch. False = not possible / not supported.</summary>
        public static bool RequestCut(string who)
        {
            var api = Api;
            if (api == null) return false;
            try
            {
                if (!_cutLooked)
                {
                    _cutLooked = true;
                    _requestCut = api.GetMethod("RequestCut", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
                return _requestCut != null && (bool)_requestCut.Invoke(null, new object[] { who });
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>(1.27.10+) Where the generator switch is.</summary>
        public static UnityEngine.Vector3? SwitchPosition => Get<UnityEngine.Vector3?>("SwitchPosition", null);
        /// <summary>(1.27.10+) The power is on and the switch can shut it down right now.</summary>
        public static bool CanCut => Get("CanCut", false);

        public static int RaidNumber => Get("RaidNumber", 0);
        public static bool BlackoutRaid => Get("BlackoutRaid", false);
        /// <summary>-1 never cut, 0 dark, 1 half, 2 full.</summary>
        public static int Power => Get("Power", -1);
        public static float DarkSince => Get("DarkSince", -1f);
        public static float BurntAt => Get("BurntAt", -1f);
        public static float KillaAt => Get("KillaAt", -1f);
        public static float EmergencyAt => Get("EmergencyAt", -1f);
    }
}
