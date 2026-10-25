using System.Text.Json;

namespace TheStalker;

public class StalkerConfig
{
    public bool Enabled { get; set; } = true;
    public double MarkedKeyChance { get; set; } = 35;
    public string Zone { get; set; } = "BotZoneBasement";
    public double HealthPercent { get; set; } = 100;
    public int Stims { get; set; } = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static StalkerConfig Load(out string? error)
    {
        error = null;
        var path = Path.Combine(Path.GetDirectoryName(typeof(StalkerConfig).Assembly.Location) ?? ".", "config.jsonc");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<StalkerConfig>(File.ReadAllText(path), JsonOptions) ?? new StalkerConfig();
            error = $"config.jsonc not found at {path}, using defaults.";
        }
        catch (Exception ex)
        {
            error = $"config.jsonc couldn't be read ({ex.Message}), using defaults.";
        }
        return new StalkerConfig();
    }
}
