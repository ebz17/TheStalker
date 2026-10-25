using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace TheStalker
{
    /// <summary>Loads the mod's WAVs and plays them through the game's own mixer (so game volume and occlusion apply).</summary>
    internal sealed class StalkerAudio
    {
        private readonly ManualLogSource _log;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedPlay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private AudioMixerGroup _world, _ambient;
        private bool _resolved;
        private readonly System.Random _rng = new System.Random();
        public float Volume = 1f;

        public StalkerAudio(ManualLogSource log)
        {
            _log = log;
        }

        public int Count => _clips.Count;

        public IEnumerator LoadFolder(string folder)
        {
            if (!Directory.Exists(folder))
            {
                _log.LogWarning($"[Stalker] sounds folder missing ({folder}) - he'll be silent.");
                yield break;
            }
            foreach (var file in Directory.GetFiles(folder, "*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                AudioClip clip = null;
                var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.WAV);
                yield return req.SendWebRequest();
                try
                {
                    if (string.IsNullOrEmpty(req.error)) clip = DownloadHandlerAudioClip.GetContent(req);
                }
                catch (Exception)
                {
                    clip = null;
                }
                if (clip == null || clip.samples == 0)
                {
                    try { clip = WavLoader.Load(file); }
                    catch (Exception ex) { _log.LogWarning($"[Stalker] couldn't load {name}: {ex.Message}"); }
                }
                req.Dispose();
                if (clip != null)
                {
                    clip.name = name;
                    Protect(clip);
                    _clips[name] = clip;
                    _files[name] = file;
                }
            }
            _log.LogInfo($"[Stalker] {_clips.Count} sounds loaded.");
        }

        private void Resolve()
        {
            if (_resolved && _world != null) return;
            _resolved = true;
            try
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (ba != null)
                {
                    _world = ba.EnvTechnicalSoundsGroup ?? ba.SimpleOccludedMixerGroup ?? ba.WorldMixer ?? ba.MasterMixerGroup;
                    _ambient = ba.NonSpatialBypassMixer ?? ba.CommonAmbientEffectsMixer ?? ba.MasterMixerGroup;
                }
            }
            catch (Exception)
            {
                // fall back below
            }
            if (_world == null || _ambient == null)
            {
                foreach (var g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                {
                    if (g != null && g.name == "CommonAmbInEffects")
                    {
                        _world = _world ?? g;
                        _ambient = _ambient ?? g;
                    }
                }
            }
        }

        /// <summary>The game clears "unused" assets when a raid loads - our clips must survive that.</summary>
        private static void Protect(AudioClip clip)
        {
            if (clip != null) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }

        private static bool Usable(AudioClip clip)
        {
            try
            {
                return clip != null && clip.samples > 0 && clip.frequency > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A playable clip - reloaded from disk if the game wiped it.</summary>
        private AudioClip Get(string name)
        {
            if (name == null || !_clips.TryGetValue(name, out var clip)) return null;
            if (Usable(clip)) return clip;
            if (!_files.TryGetValue(name, out var file)) return null;
            try
            {
                var fresh = WavLoader.Load(file);
                fresh.name = name;
                Protect(fresh);
                _clips[name] = fresh;
                _log.LogInfo($"[Stalker] sound '{name}' had been unloaded by the game - reloaded from disk ({fresh.samples} samples).");
                return Usable(fresh) ? fresh : null;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Stalker] couldn't reload '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Raid start: make sure every sound is still there before it's needed.</summary>
        public void VerifyAll()
        {
            int ok = 0, reloaded = 0;
            foreach (var name in new List<string>(_clips.Keys))
            {
                bool was = Usable(_clips[name]);
                if (Get(name) != null) { if (was) ok++; else reloaded++; }
            }
            _resolved = false;      // the mixer groups belong to the new level
            _world = _ambient = null;
            _loggedPlay.Clear();
            _log.LogInfo($"[Stalker] raid sound check: {ok} ok, {reloaded} reloaded, {_clips.Count - ok - reloaded} missing.");
        }

        private void LogPlay(string name, string how, AudioSource src, AudioClip clip)
        {
            if (!_loggedPlay.Add(name + how)) return;      // once per sound, enough to see it works
            _log.LogInfo($"[Stalker] play '{name}' {how} {clip.length:0.0}s vol {src.volume:0.00} mixer '{src.outputAudioMixerGroup?.name ?? "none"}' playing={src.isPlaying}");
        }

        /// <summary>One random clip whose name starts with prefix (e.g. "laugh_").</summary>
        public string Pick(string prefix)
        {
            var names = _clips.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return names.Count == 0 ? null : names[_rng.Next(names.Count)];
        }

        public float Length(string name) => Get(name)?.length ?? 0f;

        /// <summary>Positional sound that follows a transform (his head) while it plays.</summary>
        public AudioSource Play3D(string name, Vector3 pos, Transform follow, float volume, float minDist, float maxDist)
        {
            var clip = Get(name);
            if (clip == null) return null;
            Resolve();
            var go = new GameObject("Stalker_" + name);
            go.transform.position = pos;
            if (follow != null) go.transform.SetParent(follow, true);
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = _world;
            src.clip = clip;
            src.spatialBlend = 1f;
            src.dopplerLevel = 0f;
            src.spread = 20f;
            src.priority = 16;
            src.minDistance = minDist;
            src.maxDistance = maxDist;
            src.rolloffMode = AudioRolloffMode.Custom;
            float m = Mathf.Clamp01(minDist / maxDist);
            src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(m, 1f), new Keyframe(Mathf.Lerp(m, 1f, 0.3f), 0.65f),
                new Keyframe(Mathf.Lerp(m, 1f, 0.7f), 0.28f), new Keyframe(1f, 0f)));
            src.volume = Mathf.Clamp01(volume * Volume);
            src.Play();
            LogPlay(name, "3D", src, clip);
            UnityEngine.Object.Destroy(go, clip.length + 0.3f);
            return src;
        }

        /// <summary>Non-positional (jump-scare sting, PA).</summary>
        public AudioSource Play2D(string name, float volume)
        {
            var clip = Get(name);
            if (clip == null) return null;
            Resolve();
            var go = new GameObject("Stalker_" + name);
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = _ambient;
            src.clip = clip;
            src.spatialBlend = 0f;
            src.priority = 0;
            src.bypassReverbZones = true;
            src.volume = Mathf.Clamp01(volume * Volume);
            src.Play();
            LogPlay(name, "2D", src, clip);
            UnityEngine.Object.Destroy(go, clip.length + 0.3f);
            return src;
        }
    }
}
