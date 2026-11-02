using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaExpiryTests
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        var now = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var expiredAt = now.AddHours(-1); var connection = new Connection(() => now);
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        QuotaSnapshot Old(DateTimeOffset stamp) => new(QuotaStatus.Live,
            new(8, 300, expiredAt), new(70, 10080, now.AddDays(6)), stamp);
        service.AcceptLocalReading(new(Old(now), 1, now));
        await service.RefreshAsync(true);
        Require(service.Source == "app-server" && service.Snapshot.FiveHour?.RemainingPercent == 100,
            "A deliberate calibration must select the actual post-reset quota.");
        now = now.AddSeconds(1);
        service.AcceptLocalReading(new(Old(now), 1, now));
        Require(service.Source == "app-server" && service.Snapshot.FiveHour?.RemainingPercent == 100,
            "A newly timestamped host event carrying the old expired quota cycle must not replace calibrated quota.");
        now = now.AddSeconds(1);
        var fresh = new QuotaSnapshot(QuotaStatus.Live, new(99, 300, now.AddHours(5)), new(70, 10080, now.AddDays(6)), now);
        service.AcceptLocalReading(new(fresh, 1, now));
        Require(service.Source == "local-session" && service.Snapshot.FiveHour?.RemainingPercent == 99,
            "A real new-cycle local event must resume fast local updates after calibration.");
        connection.ResetOverride = expiredAt;
        now = now.AddSeconds(1); await service.RefreshAsync(true);
        Require(service.Source == "local-session" && service.Snapshot.FiveHour?.RemainingPercent == 99,
            "An online response containing an expired cycle must not roll back a valid local cycle either.");
        Console.WriteLine("PASS expired-cycle rollback protection and return to new-cycle host events");
    }
    sealed class Connection(Func<DateTimeOffset> clock) : ICodexConnection
    {
        public DateTimeOffset? ResetOverride;
        public event Action<string, JsonElement>? Notification { add { } remove { } }
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.FromResult(JsonSerializer.SerializeToElement(method == "account/read"
                ? (object)new { account = new { type = "chatgpt", id = "expiry-test" } }
                : new { rateLimits = new { primary = new { usedPercent = 0, windowDurationMins = 300, resetsAt = (ResetOverride ?? clock().AddHours(5)).ToUnixTimeSeconds() },
                    secondary = new { usedPercent = 30, windowDurationMins = 10080, resetsAt = clock().AddDays(6).ToUnixTimeSeconds() } } }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
