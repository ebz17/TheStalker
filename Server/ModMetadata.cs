using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace TheStalker;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.blackoutaddons.thestalker";
    public string Name { get; init; } = "The Stalker";
    public string Author { get; init; } = "Blackout Addons";
    public List<string>? Contributors { get; init; } = new();
    public Version Version { get; init; } = new("1.0.43");
    public Range SptVersion { get; init; } = new("~4.1.1");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; } = new();
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        { "com.morebotsapi.tacticaltoaster", new Range(">=2.1.0") },
    };
    public string? Url { get; init; } = "";
    public string License { get; init; } = "MIT";
}
