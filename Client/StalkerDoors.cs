using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace TheStalker
{
    /// <summary>
    /// Doors: he opens them instead of walking past or through them.
    /// Our own movement layer skips the game's door handling, so every move first asks the game's door opener
    /// (the same call the game's own move actions make). Backup: a shut door right in front of him on his way
    /// gets opened directly. While a door is opening he doesn't sprint.
    /// </summary>
    internal static partial class Stalker
    {
        public static Func<bool> OpensDoors = () => true;

        private static Vector3 _moveTo;
        private static bool _atDoorNow;
        private static Door[] _doors;
        private static float _nextDoorScan, _nextDoorCheck, _doorBusyUntil;
        private static bool _doorErrLogged, _gameDoorLogged;
        private static Door _gameDoor;
        private static readonly Dictionary<Door, float> DoorTried = new Dictionary<Door, float>();
        private static readonly Dictionary<Door, Vector3> DoorCenter = new Dictionary<Door, Vector3>();
        private static readonly NavMeshPath DoorPath = new NavMeshPath();

        private static void ResetDoors()
        {
            _doors = null;
            _nextDoorScan = _nextDoorCheck = _doorBusyUntil = 0f;
            _gameDoor = null;
            _gameDoorLogged = false;
            DoorTried.Clear();
            DoorCenter.Clear();
        }

        /// <summary>Called before every move. True while he's dealing with a door (no sprint, no looking away).</summary>
        private static bool AtDoor(BotOwner b, Vector3 to)
        {
            _moveTo = to;
            if (!OpensDoors() || Body == null) return false;
            bool busy = false;
            try
            {
                var opener = b.DoorOpener;
                var st = opener.UpdateDoorInteractionStatus();
                busy = opener.Interacting || b.Mover.CurrentState == EBotMoverState.NearDoor;
                if (opener.Interacting && opener._lastInteractingDoor != null && opener._lastInteractingDoor != _gameDoor)
                {
                    _gameDoor = opener._lastInteractingDoor;
                    LogDoor(_gameDoor, opener._lastInteractingDoorState == EDoorState.Shut ? "closes" : "opens");
                    if (!_gameDoorLogged)
                    {
                        _gameDoorLogged = true;
                        Dbg("the game's door handling works for him");
                    }
                }
                // (st == OpeningDoor only means "a door somewhere near" - in Labs that's most of the time; it no longer
                //  stops his sprint. Only actually being at / working a door does.)
            }
            catch (Exception ex)
            {
                if (!_doorErrLogged)
                {
                    _doorErrLogged = true;
                    Log.LogWarning($"[Stalker] door handling error (backup only from now): {ex.Message}");
                }
            }
            if (busy) return true;
            return OpenDoorAhead() || Time.time < _doorBusyUntil;
        }

        /// <summary>Backup: a shut door within reach, in the direction he's walking, gets opened.</summary>
        private static bool OpenDoorAhead()
        {
            if (Time.time < _nextDoorCheck) return false;
            _nextDoorCheck = Time.time + 0.2f;
            if (_doors == null || Time.time >= _nextDoorScan)
            {
                _nextDoorScan = Time.time + 60f;
                try { _doors = UnityEngine.Object.FindObjectsOfType<Door>(); }
                catch (Exception) { _doors = Array.Empty<Door>(); }
                DoorCenter.Clear();
            }
            if (_doors.Length == 0) return false;

            Vector3 pos = Body.Position;
            Vector3 next = _moveTo;
            try
            {
                if (NavMesh.CalculatePath(pos, _moveTo, NavMesh.AllAreas, DoorPath) && DoorPath.corners.Length > 1)
                    next = DoorPath.corners[1];
            }
            catch (Exception)
            {
                // straight at the target then
            }
            Vector3 dir = next - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f) return false;
            dir.Normalize();

            foreach (var door in _doors)
            {
                if (door == null || !door.Operatable || door.DoorState != EDoorState.Shut) continue;
                Vector3 c = Center(door);
                Vector3 to = c - pos;
                if (Mathf.Abs(to.y - 0.9f) > 1.6f) continue;
                to.y = 0f;
                float d = to.magnitude;
                if (d > 1.7f || d < 0.05f) continue;
                if (Vector3.Dot(dir, to / d) < 0.35f) continue;
                if (DoorTried.TryGetValue(door, out var t) && Time.time - t < 4f) continue;
                DoorTried[door] = Time.time;
                try
                {
                    Body.CurrentManagedState.StartDoorInteraction(door, new InteractionResult(EInteractionType.Open), null);
                    _doorBusyUntil = Time.time + 1.2f;
                    LogDoor(door, "opens");
                    return true;
                }
                catch (Exception ex)
                {
                    Dbg($"couldn't open a door: {ex.Message}");
                }
            }
            return false;
        }

        private static Vector3 Center(Door door)
        {
            if (DoorCenter.TryGetValue(door, out var c)) return c;
            c = door.transform.position;
            try
            {
                var col = door.GetComponentInChildren<Collider>();
                if (col != null) c = col.bounds.center;
            }
            catch (Exception)
            {
                // the hinge will do
            }
            DoorCenter[door] = c;
            return c;
        }

        private static void LogDoor(Door door, string what)
        {
            float d = _me != null ? (door.transform.position - _me.Position).magnitude : -1f;
            Log.LogInfo($"[Stalker] {what} a door ({d:0} m from you, {State}).");
        }
    }
}
