using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using EFT;

namespace TheStalker
{
    /// <summary>Takes over his whole brain (above every Cultist layer) and lets Stalker.Drive decide.</summary>
    internal sealed class StalkerLayer : CustomLayer
    {
        public StalkerLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "TheStalker";
        public override bool IsActive() => Stalker.Controls(BotOwner);
        public override Action GetNextAction() => new Action(typeof(StalkerLogic), Stalker.State.ToString());
        public override bool IsCurrentActionEnding() => !Stalker.Controls(BotOwner);
    }

    internal sealed class StalkerLogic : CustomLogic
    {
        public StalkerLogic(BotOwner bot) : base(bot) { }

        public override void Start()
        {
            BotOwner.PatrollingData?.Pause();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData?.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Stalker.Drive(BotOwner);
            }
            catch (Exception ex)
            {
                Stalker.Log.LogWarning($"[Stalker] brain error: {ex.Message}");
            }
        }
    }

    internal static class StalkerLayerSetup
    {
        public static bool Install(ManualLogSource log)
        {
            try
            {
                BrainManager.AddCustomLayer(typeof(StalkerLayer), new List<string> { "SectantWarrior" }, 250,
                    new List<WildSpawnType> { (WildSpawnType)Stalker.Role });
                log.LogInfo("[Stalker] brain layer registered (BigBrain).");
                return true;
            }
            catch (Exception ex)
            {
                log.LogError($"[Stalker] brain layer not registered ({ex.Message}) - he'd act like a normal Cultist, so he's turned off.");
                return false;
            }
        }
    }
}
