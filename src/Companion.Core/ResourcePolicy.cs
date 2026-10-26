namespace CodexCompanion.Core;

public static class ResourcePolicy
{
    public static double FitDisplayHeight(double requestedHeight, PixelRect work, double dpiScale) =>
        Math.Min(requestedHeight, Math.Max(1, Math.Min(work.Width, work.Height) / dpiScale - 12));
    public static int DecodeHeight(double displayHeight, double dpiScale) =>
        (int)Math.Clamp(Math.Ceiling(displayHeight * Math.Clamp(dpiScale, 1, 3)), 96, 960);
    public static TimeSpan TrackingInterval(bool visible) => TimeSpan.FromMilliseconds(visible ? 250 : 1000);
}

public static class HostIdentity
{
    public static bool IsCodexPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return (name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)) &&
            (normalized.Contains("/WindowsApps/OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("/OpenAI/Codex/app-", StringComparison.OrdinalIgnoreCase));
    }
}
