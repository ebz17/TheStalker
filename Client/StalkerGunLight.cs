using System;
using System.Collections.Generic;
using UnityEngine;

namespace TheStalker
{
    /// <summary>
    /// Point your light at him (within 30 m, nothing in between) and it stutters the whole time it's on him: on, off,
    /// weak, off... and settles about half a second after you look away. Any light you carry counts - a pistol light
    /// too, however short its beam. Only the brightness is touched (and put back exactly), never its on/off switch.
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<bool> GunLightFlicker = () => true;

        private static readonly Dictionary<Light, float> Flick = new Dictionary<Light, float>();
        private static float _flickUntil = -1f, _nextFlick;

        /// <summary>One of your lights is switched on and pointed at him (a generous cone, any beam length), clear line.</summary>
        private static bool AimedAtHim()
        {
            if (Body == null || _me == null) return false;
            Vector3 him = Chest(Body);
            foreach (var l in PlayerLights)
            {
                if (!Aims(l, him)) continue;
                return true;
            }
            foreach (var l in Flick.Keys)
            {
                if (Aims(l, him)) return true;          // already flickering (its brightness may be 0 right now)
            }
            return false;
        }

        private static bool Aims(Light l, Vector3 him)
        {
            if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) return false;
            Vector3 d = him - l.transform.position;
            if (d.magnitude > 30f) return false;
            if (Vector3.Angle(l.transform.forward, d) > Mathf.Max(15f, l.spotAngle * 0.5f)) return false;
            return Clear(l.transform.position, him);
        }

        /// <summary>Called every perception tick while your light is on him: keeps it stuttering.</summary>
        private static void KeepGunFlicker()
        {
            if (!GunLightFlicker()) return;
            bool starting = Flick.Count == 0;
            foreach (var l in PlayerLights)
            {
                if (l != null && !Flick.ContainsKey(l) && l.intensity > 0.01f) Flick[l] = l.intensity;
            }
            if (Flick.Count == 0) return;
            _flickUntil = Mathf.Max(_flickUntil, Time.time + (starting ? 0.9f : 0.5f));
            if (starting && Time.time >= _nextFlick)
            {
                _nextFlick = Time.time + 10f;           // log once in a while, not every time
                Dbg($"your light stutters on him ({_dist:0} m)");
            }
        }

        /// <summary>Every frame (plugin Update).</summary>
        private static void GunFlickerTick()
        {
            if (Flick.Count == 0) return;
            if (Time.time >= _flickUntil)
            {
                StopGunFlicker();
                return;
            }
            int slot = Mathf.FloorToInt(Time.time / 0.06f);
            int h = slot * 73856093 ^ 19349663;
            float r = (h & 0xFFFF) / 65535f;
            float f = r < 0.45f ? 0f : r < 0.75f ? 0.25f + r * 0.4f : 1f;
            foreach (var kv in Flick)
            {
                if (kv.Key != null) kv.Key.intensity = kv.Value * f;
            }
        }

        private static void StopGunFlicker()
        {
            foreach (var kv in Flick)
            {
                if (kv.Key != null) kv.Key.intensity = kv.Value;
            }
            Flick.Clear();
            _flickUntil = -1f;
        }
    }
}
