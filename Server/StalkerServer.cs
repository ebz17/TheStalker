using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace TheStalker;

/// <summary>
/// Registers the "bossStalker" bot type with MoreBotsAPI (type + config from db/bots), at MoreBotsAPI's bot-loading
/// step. MoreBotsAPI is used through reflection, so this mod doesn't have to be rebuilt for each MoreBotsAPI version.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 80085)]
public class StalkerBotType(IServiceProvider services, BotTable bots, BotConfig botConfig, ISptLogger<StalkerBotType> logger) : IOnLoad
{
    public const string Log = "[The Stalker]";
    public const int TypeValue = 848470;
    public const string TypeName = "bossStalker";
    public static bool Loaded;
    // Zagustin, eTG-c, Trimadol, Propital, Perfotoran (healing) and Obdolbos 2, Adrenaline, PNB, SJ6 (his highs)
    private static readonly string[] StimTpls =
    {
        "5c0e533786f7747fa23f4d47", "5c0e534186f7747fa1419867", "637b620db7afa97bfc3d7009", "5c0e530286f7747fa1419862", "637b6251104668754b72f8f9",
        "637b60c3b7afa97bfc3d7001", "5c10c8fd86f7743d7d706df3", "637b6179104668754b72f8f5", "5c0e531d86f7747fa23f4d42",
    };

    private object? Service(string typeName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "MoreBotsServer")
            .Select(a => a.GetType(typeName))
            .FirstOrDefault(t => t != null);
        return type == null ? null : services.GetService(type);
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cfg = StalkerConfig.Load(out var err);
        if (err != null) logger.Warning($"{Log} {err}");
        if (!cfg.Enabled)
        {
            logger.Info($"{Log} disabled in config.jsonc.");
            return;
        }
        try
        {
            var api = Service("MoreBotsServer.MoreBotsAPI");
            var typeService = Service("MoreBotsServer.Services.MoreBotsCustomBotTypeService");
            if (api == null || typeService == null)
            {
                logger.Error($"{Log} MoreBotsAPI not found - the Stalker can't be added. Install MoreBotsAPI 2.1+.");
                return;
            }
            var load = api.GetType().GetMethod("LoadBots", new[] { typeof(Assembly) });
            if (load?.Invoke(api, new object[] { Assembly.GetExecutingAssembly() }) is Task t) await t;
            typeService.GetType().GetMethod("AddCustomWildSpawnTypeNames")?
                .Invoke(typeService, new object[] { new Dictionary<int, string> { { TypeValue, TypeName } } });

            // hostile to everyone, like the Cultists
            var factions = Service("MoreBotsServer.Services.FactionService");
            var addEnemy = factions?.GetType().GetMethod("AddEnemyByFaction", new[] { typeof(IEnumerable<string>), typeof(string) });
            if (addEnemy != null)
            {
                foreach (var f in new[] { "pmcs", "savage", "raiders", "rogues", "cultists", "infected", "killaTagilla", "blackdiv" })
                {
                    try { addEnemy.Invoke(factions, new object[] { new List<string> { TypeName }, f }); }
                    catch (Exception) { /* faction not in this install */ }
                }
            }

            if (!bots.Types.TryGetValue(TypeName.ToLowerInvariant(), out var type) || type == null)
            {
                logger.Error($"{Log} bot type didn't load (db/bots/types/bossStalker.json).");
                return;
            }
            // the marked key goes in his vest (his stims fill his pockets first)
            var vest = type.BotGeneration?.Items?.VestLoot;
            if (vest != null)
            {
                int chance = (int)Math.Round(Math.Clamp(cfg.MarkedKeyChance, 0, 100));
                vest.Weights = new Dictionary<double, double> { { 0, 100 - chance }, { 1, chance } };
            }
            // health: % of the json values
            double hp = Math.Clamp(cfg.HealthPercent, 25, 400) / 100.0;
            double total = 0;
            foreach (var parts in type.BotHealth?.BodyParts ?? Enumerable.Empty<SPTarkov.Server.Core.Models.Eft.Common.Tables.BodyPart>())
            {
                foreach (var mm in new[] { parts.Head, parts.Chest, parts.Stomach, parts.LeftArm, parts.RightArm, parts.LeftLeg, parts.RightLeg })
                {
                    if (mm == null) continue;
                    mm.Min = Math.Max(1, Math.Round(mm.Min * hp));
                    mm.Max = Math.Max(1, Math.Round(mm.Max * hp));
                    total += mm.Max;
                }
                break;
            }
            // stims: how many he carries, picked from the nine
            int stims = Math.Clamp(cfg.Stims, 0, StimTpls.Length);
            var stimGen = type.BotGeneration?.Items?.Stims;
            if (stimGen != null) stimGen.Weights = new Dictionary<double, double> { { stims, 1 } };
            // one of each: never two of the same
            try
            {
                var limits = botConfig.ItemSpawnLimits;
                if (!limits.TryGetValue(TypeName.ToLowerInvariant(), out var mine) || mine == null) limits[TypeName.ToLowerInvariant()] = mine = new();
                foreach (var tpl in StimTpls) mine[tpl] = 1;
            }
            catch (Exception ex)
            {
                logger.Warning($"{Log} couldn't set the one-of-each stim limit: {ex.Message}");
            }

            Loaded = true;
            logger.Success($"{Log} bot type registered ({total:0} HP, {stims} stim(s), marked key chance {cfg.MarkedKeyChance:0}%).");
        }
        catch (Exception ex)
        {
            logger.Error($"{Log} couldn't register the bot type: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}

/// <summary>
/// Adds his (held) wave to Labs and every other map. The client mod decides if and when he comes in
/// (Labs as before; other maps only in night raids, the Labyrinth any time).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 100)]
public class StalkerWave(LocationTable locations, ISptLogger<StalkerWave> logger) : IOnLoad
{
    public const string SptId = "the-stalker";
    private static readonly string[] Maps =
    {
        "laboratory", "bigmap", "factory4_day", "factory4_night", "interchange", "lighthouse", "rezervbase",
        "shoreline", "tarkovstreets", "woods", "sandbox", "sandbox_high", "labyrinth",
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cfg = StalkerConfig.Load(out _);
        if (!cfg.Enabled || !StalkerBotType.Loaded) return Task.CompletedTask;
        var added = new List<string>();
        foreach (var map in Maps)
        {
            try
            {
                var loc = locations.GetLocation(map)?.Base;
                var spawns = loc?.BossLocationSpawn;
                if (loc == null || spawns == null) continue;
                string zone = map == "laboratory" ? cfg.Zone : Zones(loc);
                if (string.IsNullOrEmpty(zone)) continue;
                spawns.RemoveAll(s => s?.SptId == SptId);
                spawns.Add(Wave(zone));
                added.Add(map);
            }
            catch (Exception ex)
            {
                logger.Warning($"{StalkerBotType.Log} couldn't add him to {map}: {ex.Message}");
            }
        }
        if (!added.Contains("laboratory")) logger.Error($"{StalkerBotType.Log} Labs boss spawn table not found - he won't come to Labs.");
        logger.Info($"{StalkerBotType.Log} added to {added.Count} maps ({string.Join(", ", added)}). He wakes up in the dark.");
        return Task.CompletedTask;
    }

    /// <summary>Where he can arrive on a map: its bot zones, no sniper perches. The client moves him out of your sight anyway.</summary>
    private static string Zones(SPTarkov.Server.Core.Models.Eft.Common.LocationBase loc)
    {
        var zones = (loc.OpenZones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(z => z.IndexOf("snipe", StringComparison.OrdinalIgnoreCase) < 0)
            .Distinct()
            .ToList();
        if (zones.Count == 0)
        {
            var boss = loc.BossLocationSpawn?.FirstOrDefault(b => !string.IsNullOrEmpty(b?.BossZone) && b.SptId != SptId);
            if (boss != null) return boss.BossZone!;
        }
        return string.Join(",", zones);
    }

    private static BossLocationSpawn Wave(string zone) => new()
    {
        SptId = SptId,
        BossName = StalkerBotType.TypeName,
        BossChance = 100,          // the client rolls the real chance and holds him back until he wakes
        BossDifficulty = "normal",
        BossEscortAmount = "0",
        BossEscortDifficulty = "normal",
        BossEscortType = "followerBully",
        BossZone = zone,
        IsBossPlayer = false,
        IsRandomTimeSpawn = false,
        Time = -1,
        Delay = 0,
        TriggerId = "",
        TriggerName = "",
        Supports = null!,
        ForceSpawn = true,
        IgnoreMaxBots = true,
        SpawnMode = new List<string> { "regular", "pve" },
    };
}
