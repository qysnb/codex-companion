using System.Text.Json;
namespace CodexCompanion.Core;

public sealed record CompanionSettings
{
    public static readonly string[] Characters = ["elegant", "mischievous", "cute", "fable", "haiku", "pro", "flash"];
    public static readonly (string Id, int Height)[] Sizes = [("small", 180), ("medium", 240), ("large", 320), ("xlarge", 420)];
    public static readonly string[] Languages = ["zh-CN", "en", "ja"];
    public string Character { get; init; } = "cute";
    public string Language { get; init; } = "zh-CN";
    public string Size { get; init; } = "medium";
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public bool ReducedMotion { get; init; }
    public bool Startup { get; init; }
    public bool QuotaResetNotifications { get; init; } = true;
    public int RefreshIntervalSeconds { get; init; } = 30;
    // 0 stays open until dismissed; legacy -1 normalizes to the new 10s default.
    public int BubbleDurationSeconds { get; init; } = 10;
    public TimeSpan? GetBubbleDuration(BubbleKind kind) => BubbleDurationSeconds == 0 ? null
        : TimeSpan.FromSeconds(BubbleDurationSeconds is >= 1 and <= 86400 ? BubbleDurationSeconds : 10);
    public string QuotaTextColor { get; init; } = "#3D2F42";
    public string QuotaBarColor { get; init; } = "#00A9D6";
    public static bool IsThemeColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public int Height => Sizes.FirstOrDefault(size => size.Id == Size, Sizes[1]).Height;
    public CompanionSettings Normalize() => this with
    {
        Character = Characters.Contains(Character) ? Character : "cute",
        Language = Languages.Contains(Language) ? Language : "zh-CN",
        Size = Sizes.Any(size => size.Id == Size) ? Size : "medium",
        QuotaTextColor = IsThemeColor(QuotaTextColor) ? QuotaTextColor.ToUpperInvariant() : "#3D2F42",
        QuotaBarColor = IsThemeColor(QuotaBarColor) ? QuotaBarColor.ToUpperInvariant() : "#00A9D6",
        RefreshIntervalSeconds = RefreshIntervalSeconds is >= 5 and <= 3600 ? RefreshIntervalSeconds : 30,
        BubbleDurationSeconds = BubbleDurationSeconds is >= 0 and <= 86400 ? BubbleDurationSeconds : 10,
        OffsetX = double.IsFinite(OffsetX) && Math.Abs(OffsetX) <= 10000 ? OffsetX : 0,
        OffsetY = double.IsFinite(OffsetY) && Math.Abs(OffsetY) <= 10000 ? OffsetY : 0
    };
}
public sealed class SettingsStore
{
    public string Path { get; }
    public SettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexCompanion", "settings.json");
    public CompanionSettings Load()
    {
        try { return (JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(Path)) ?? new()).Normalize(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(CompanionSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Path, true);
    }
}
