namespace CodexCompanion.Core;
public record PixelRect(double X, double Y, double Width, double Height);
public static class CompanionPlacement
{
    public static PixelRect Place(PixelRect codex, PixelRect work, double width, double height, double scale, double offsetX, double offsetY)
    {
        width *= scale; height *= scale;
        var fit = Math.Min(1, Math.Min(work.Width / width, work.Height / height));
        width *= fit; height *= fit;
        var x = codex.X + codex.Width - width - 20 * scale + offsetX * scale;
        var y = codex.Y + codex.Height - height - 20 * scale + offsetY * scale;
        return new(Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height)), width, height);
    }
}
public sealed class BubbleLifetime
{
    public TimeSpan Remaining { get; private set; }
    public bool IsPermanent { get; private set; }
    public bool IsOpen => Remaining > TimeSpan.Zero;
    public void Open(TimeSpan duration) { IsPermanent = false; Remaining = duration; }
    public void OpenPermanent() { IsPermanent = true; Remaining = TimeSpan.MaxValue; }
    public void Close() { IsPermanent = false; Remaining = TimeSpan.Zero; }
    public void Advance(TimeSpan elapsed, bool hovering) { if (!IsPermanent && !hovering && elapsed > TimeSpan.Zero) Remaining = Remaining > elapsed ? Remaining - elapsed : TimeSpan.Zero; }
}
public static class CompanionCommand
{
    public static string? Validate(string command, string? value) => command switch
    {
        "set-character" => CompanionSettings.Characters.Contains(value) ? null : "Invalid character.",
        "set-language" => CompanionSettings.Languages.Contains(value) ? null : "Invalid language.",
        "set-refresh-interval" => int.TryParse(value, out var seconds) && seconds is >= 5 and <= 3600 ? null : "Refresh interval must be 5–3600 seconds.",
        "show" or "hide" or "exit" or "refresh-quota" or "get-status" => value is null ? null : "This command does not take a value.",
        _ => "Unknown command."
    };
}
