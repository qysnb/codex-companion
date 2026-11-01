using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaTimestampTests
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static QuotaSnapshot Value(DateTimeOffset time, double remaining) => new(QuotaStatus.Live, new(remaining, 300, time.AddHours(2)), new(75, 10080, time.AddDays(4)), time);
    static async Task RunAsync()
    {
        var time = DateTimeOffset.UtcNow; var dataTime = time.AddHours(-1); var connection = new Fake();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => time)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        var local = Value(dataTime, 44);
        service.AcceptLocalReading(new(local, 100, time));
        Require(service.LastReadAt == time && service.Snapshot.UpdatedAt == dataTime,
            "Successful read time must be separate from the original host event time.");
        var firstRead = time; time = time.AddSeconds(5);
        service.AcceptLocalReading(new(local, 100, time, true));
        Require(service.LastReadAt == time && service.Snapshot.UpdatedAt == dataTime && service.LastReadAt > firstRead,
            "A successful local reread must advance the displayed read time without forging new quota data.");
        time = time.AddSeconds(1); await service.RefreshAsync(true);
        Require(service.LastReadAt == time && service.Source == "app-server", "Successful online reads must use the same read clock.");
        var onlineTime = time; time = time.AddSeconds(1);
        service.AcceptLocalReading(new(local, 100, time, true));
        Require(service.LastReadAt == time && service.Source == "app-server" && service.Snapshot.UpdatedAt == onlineTime && service.Snapshot.FiveHour?.RemainingPercent == 30,
            "Latest read activity may come from either source, but rereading old local data must not replace newer online values.");
        var readBeforeFailure = service.LastReadAt; connection.Fail = true; time = time.AddSeconds(1);
        await service.RefreshAsync(true);
        Require(service.LastReadAt == readBeforeFailure, "Failed reads must not advance the success timestamp.");
        service.InvalidateLocalReading(101, true);
        Require(service.LastReadAt is null, "Account invalidation must clear read timestamps along with quota.");

        await using var equal = new QuotaService(_ => Task.FromResult<ICodexConnection>(new Fake()), () => time)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        var sameTime = Value(time, 90); equal.AcceptLocalReading(new(sameTime, 200, time));
        await equal.RefreshAsync(true);
        Require(equal.Source == "app-server", "Equal data timestamps must not give local data a fixed priority over the last successful online read.");
        equal.AcceptLocalReading(new(sameTime, 200, time, true));
        Require(equal.Source == "local-session", "Equal timestamps must also allow the next successful local read to win.");
        await RecheckWithoutNewEvents();
        Console.WriteLine("PASS separate read/data timestamps, source-neutral success clock, equal-time ordering, no old-data rollback, failure/account safety and idle local rereads");
    }
    static async Task RecheckWithoutNewEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "companion-read-time-" + Guid.NewGuid());
        var dir = Path.Combine(root, "sessions"); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rollout-clock.jsonl"); await File.WriteAllTextAsync(path, "\n");
        try
        {
            await using var source = new LocalQuotaMonitor(root) { IntervalMilliseconds = 200 };
            var initial = new TaskCompletionSource<LocalQuotaReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            var repeated = new TaskCompletionSource<LocalQuotaReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Reading += r => { if (r.IsRecheck) repeated.TrySetResult(r); else initial.TrySetResult(r); };
            await source.StartAsync();
            for (var i = 0; source.Stats.State != "listening" && i < 100; i++) await Task.Delay(10);
            var stamp = DateTimeOffset.UtcNow;
            var record = JsonSerializer.Serialize(new { timestamp = stamp, type = "event_msg", payload = new { type = "token_count", rate_limits = new { limit_id = "codex", primary = new { used_percent = 56, window_minutes = 300, resets_at = stamp.AddHours(2).ToUnixTimeSeconds() } } } });
            // Ensure a bookmarked quota record after unrelated and partial records.
            await File.AppendAllTextAsync(path, "{\"type\":\"response_item\",\"payload\":\"ignored\"}\n" + record[..50]);
            await Task.Delay(50); await File.AppendAllTextAsync(path, record[50..] + "\n");
            var first = await initial.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var again = await repeated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(again.ObservedAt > first.ObservedAt && again.Snapshot == first.Snapshot && again.Snapshot.UpdatedAt == stamp,
                "An unchanged quota record must be physically reread with a new receipt time and unchanged data time.");
            var end = DateTimeOffset.UtcNow.AddSeconds(2);
            while (source.Stats.Rechecks == 0 && DateTimeOffset.UtcNow < end) await Task.Delay(10);
            Require(source.Stats.Rechecks > 0 && source.Stats.Readings == 1 && source.Stats.LastReadAt > source.Stats.LastReceivedAt,
                "Idle rereads must be distinct from new host events.");
            File.Delete(path); await Task.Delay(250); var afterDelete = source.Stats.LastReadAt;
            await Task.Delay(450);
            Require(source.Stats.LastReadAt == afterDelete, "A missing file must not produce a fake successful read.");
        }
        finally { Directory.Delete(root, true); }
    }
    sealed class Fake : ICodexConnection
    {
        public bool Fail;
        public event Action<string, JsonElement>? Notification { add { } remove { } }
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("test failure");
            return Task.FromResult(method == "account/read" ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", id = "test-account" } })
                : JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = 70, windowDurationMins = 300 } } }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
