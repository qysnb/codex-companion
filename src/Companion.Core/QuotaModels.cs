using System.Text.Json;
namespace CodexCompanion.Core;
public record QuotaWindow(double RemainingPercent, int WindowMinutes, DateTimeOffset? ResetsAt);
public enum QuotaStatus { Loading, Live, Stale, NotLoggedIn, Unavailable }
public enum QuotaFailureKind { None, Timeout, Connection, InvalidResponse }
public record QuotaSnapshot(QuotaStatus Status, QuotaWindow? FiveHour, QuotaWindow? Weekly, DateTimeOffset? UpdatedAt, string? Error = null,
    QuotaFailureKind FailureKind = QuotaFailureKind.None, DateTimeOffset? RetryAt = null);
public static class QuotaParser
{
    public static QuotaSnapshot Parse(JsonElement payload, DateTimeOffset now)
    {
        JsonElement bucket = default;
        if (payload.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        { if (buckets.TryGetProperty("codex", out var codex)) bucket = codex; }
        else if (payload.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object &&
                 (!legacy.TryGetProperty("limitId", out var id) || id.GetString() == "codex")) bucket = legacy;
        QuotaWindow? five = null, weekly = null;
        if (bucket.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object ||
                    !w.TryGetProperty("windowDurationMins", out var d) || !d.TryGetInt32(out var duration) ||
                    !w.TryGetProperty("usedPercent", out var u) || !u.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
                DateTimeOffset? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var seconds))
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
                var window = new QuotaWindow(Math.Clamp(100 - used, 0, 100), duration, reset);
                if (duration == 300) five = window;
                if (duration == 10080) weekly = window;
            }
        return new(five is not null || weekly is not null ? QuotaStatus.Live : QuotaStatus.Unavailable, five, weekly, now,
            five is null && weekly is null ? "Codex quota windows are unavailable." : null);
    }
}
