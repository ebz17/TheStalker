using System.Collections.Generic;
using Mono.Cecil;
using MoreBotsAPI;

namespace TheStalkerPrepatch
{
    /// <summary>
    /// Adds "bossStalker" to the game's bot types (through MoreBotsAPI). He thinks with the Cultist brain underneath;
    /// The Stalker plugin drives what he actually does.
    /// </summary>
    public static class StalkerTypePatch
    {
        public const int StalkerType = 848470;
        private const int SectantWarriorBrain = 20;   // EFT.WildSpawnType.sectantWarrior

        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        public static void Patch(ref AssemblyDefinition assembly)
        {
            var stalker = new CustomWildSpawnType(StalkerType, "bossStalker", "Boss", SectantWarriorBrain,
                isBoss: true, isFollower: false, isHostileToEverybody: true);
            stalker.SetCountAsBossForStatistics(true);
            stalker.SetShouldUseFenceNoBossAttack(false, false);
            CustomWildSpawnTypeManager.RegisterWildSpawnType(stalker, assembly);
        }
    }
}
