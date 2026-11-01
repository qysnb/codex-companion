using System.Text;
using System.Text.Json;
using CodexCompanion.Core;

public static class LocalQuotaTests
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static string Record(DateTimeOffset time, double used = 12, string id = "codex") => JsonSerializer.Serialize(new {
        timestamp = time, type = "event_msg", payload = new { type = "token_count", info = new { total_tokens = 42 },
            rate_limits = new { limit_id = id, primary = new { used_percent = used, window_minutes = 300, resets_at = time.AddHours(2).ToUnixTimeSeconds() },
                secondary = new { used_percent = 25, window_minutes = 10080, resets_at = time.AddDays(4).ToUnixTimeSeconds() } } } });
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = LocalQuotaRecordParser.Parse(Encoding.UTF8.GetBytes(Record(now)));
        Require(parsed is { FiveHour.RemainingPercent: 88, Weekly.RemainingPercent: 75 } && parsed.UpdatedAt == now,
            "Native host quota records must preserve both windows and the original timestamp.");
        Require(LocalQuotaRecordParser.Parse(Encoding.UTF8.GetBytes(Record(now, id: "other"))) is null, "Other quota buckets must not replace codex.");
        foreach (var invalid in new[] { "{}", "{", "{\"type\":\"response_item\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{}}}",
            Record(now).Replace("\"rate_limits\":{", "\"rate_limits\":null,\"discard\":{") })
            Require(LocalQuotaRecordParser.Parse(Encoding.UTF8.GetBytes(invalid)) is null, "Non-quota or malformed records must be ignored.");
        var decoder = new LocalQuotaDecoder(); var values = new List<QuotaSnapshot>();
        var bytes = Encoding.UTF8.GetBytes(Record(now) + "\n");
        foreach (var b in bytes) decoder.Feed(new[] { b }, values.Add);
        Require(values.Count == 1, "A partial UTF-8/JSON record must publish only after its newline.");
        decoder.Feed(Encoding.UTF8.GetBytes("{\"timestamp\":\"x\",\"type\":\"response_item\",\"payload\":\"" + new string('x', 300_000) + "\"}\n" + Record(now.AddSeconds(1)) + "\n"), values.Add);
        Require(values.Count == 2 && decoder.CapacityBytes <= 8192 && decoder.BufferedBytes == 0, "Large unrelated records must not grow the buffer or hide the next quota event.");
        decoder.Feed(Encoding.UTF8.GetBytes(Record(now).TrimEnd('}') + new string(' ', 9000) + "}}\n" + Record(now.AddSeconds(2)) + "\n"), values.Add);
        Require(values.Count == 3, "Oversized quota records must be skipped and framing must recover.");
        Console.WriteLine("PASS local quota header gate, native schema, original timestamps, fragmented writes and bounded record recovery");
        await MonitorReadsOnlyNewAppends();
        await ConfigurableCadence();
    }
    static async Task MonitorReadsOnlyNewAppends()
    {
        var root = Path.Combine(Path.GetTempPath(), "companion-local-events-" + Guid.NewGuid());
        var sessions = Path.Combine(root, "sessions", "2026", "11", "01"); Directory.CreateDirectory(sessions);
        var file = Path.Combine(sessions, "rollout-test.jsonl");
        await File.WriteAllTextAsync(file, Record(DateTimeOffset.UtcNow.AddHours(-1), 99) + "\n");
        var resumed = Path.Combine(sessions, "rollout-resumed.jsonl");
        await File.WriteAllTextAsync(resumed, Record(DateTimeOffset.UtcNow.AddDays(-1), 98) + "\n");
        File.SetLastWriteTimeUtc(resumed, DateTime.UtcNow.AddDays(-1));
        try
        {
            await using var source = new LocalQuotaMonitor(root);
            var readings = new System.Collections.Concurrent.ConcurrentQueue<LocalQuotaReading>();
            var invalidated = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Reading += reading => { if (!reading.IsRecheck) readings.Enqueue(reading); };
            source.Invalidated += (generation, accountChanged) => { if (accountChanged) invalidated.TrySetResult(generation); };
            await source.StartAsync();
            await Until(() => source.Stats.State == "listening");
            await Task.Delay(80);
            Require(readings.IsEmpty, "Attaching must not replay old-account history.");
            var stamp = DateTimeOffset.UtcNow;
            var record = Record(stamp, 20);
            await File.AppendAllTextAsync(file, record[..70]);
            await Task.Delay(100); Require(readings.IsEmpty, "An unfinished append must not publish.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await File.AppendAllTextAsync(file, record[70..] + "\n");
            await Until(() => readings.Count == 1);
            Require(readings.TryPeek(out var first) && first.Snapshot.FiveHour?.RemainingPercent == 80 && first.Snapshot.UpdatedAt == stamp,
                "A real append must deliver its quota without a network request.");
            Require(timer.Elapsed < TimeSpan.FromSeconds(2), "Local subscription must not wait for the online refresh interval.");
            var other = Path.Combine(sessions, "rollout-new.jsonl");
            await File.WriteAllTextAsync(other, Record(DateTimeOffset.UtcNow, 21) + "\n");
            await Until(() => readings.Count == 2);
            await File.WriteAllTextAsync(file, Record(DateTimeOffset.UtcNow, 22) + "\n");
            await Until(() => readings.Count == 3);
            await File.WriteAllTextAsync(Path.Combine(root, "auth.json"), "credentials must never be parsed");
            var generation = await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            await File.AppendAllTextAsync(file, Record(DateTimeOffset.UtcNow, 23) + "\n");
            await Until(() => readings.Count == 4);
            Require(readings.Last().Generation >= generation && source.Stats.RetainedBufferBytes <= LocalQuotaMonitor.MaxTrackedFiles * 8192,
                "Auth metadata invalidation must create a new generation and keep memory bounded.");
            // An old, previously inactive chat is outside the startup baseline.
            var beforeResume = readings.Count;
            await File.AppendAllTextAsync(resumed, Record(DateTimeOffset.UtcNow, 24) + "\n");
            await Until(() => readings.Count > beforeResume);
            Require(readings.Last().Snapshot.FiveHour?.RemainingPercent == 76, "Resuming an older chat must deliver fresh events without replaying its old quota.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS real filesystem subscription, startup history exclusion, partial writes, new/truncated files, metadata-only auth invalidation and disposal");
    }
    static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(4);
        while (!condition()) { if (DateTime.UtcNow > end) throw new Exception("Local quota event deadline elapsed."); await Task.Delay(10); }
    }
    static async Task ConfigurableCadence()
    {
        var root = Path.Combine(Path.GetTempPath(), "companion-local-cadence-" + Guid.NewGuid());
        var dir = Path.Combine(root, "sessions"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "rollout-cadence.jsonl"); await File.WriteAllTextAsync(file, "\n");
        try
        {
            await using var source = new LocalQuotaMonitor(root) { IntervalMilliseconds = 400 };
            foreach (var invalid in new[] { -1, 1, 99, int.MaxValue })
            {
                try { source.IntervalMilliseconds = invalid; throw new Exception("Invalid local cadence was accepted."); }
                catch (ArgumentOutOfRangeException) { }
            }
            var values = new System.Collections.Concurrent.ConcurrentQueue<(LocalQuotaReading Reading, long Tick)>();
            source.Reading += value => { if (!value.IsRecheck) values.Enqueue((value, Environment.TickCount64)); };
            await source.StartAsync(); await Until(() => source.Stats.State == "listening");
            var now = DateTimeOffset.UtcNow;
            var unrelated = "{\"type\":\"response_item\",\"payload\":\"" + new string('x', 1_100_000) + "\"}\n";
            await File.AppendAllTextAsync(file, Record(now, 11) + "\n" + unrelated + Record(now.AddMilliseconds(1), 12) + "\n" + Record(now.AddMilliseconds(2), 13) + "\n");
            await Task.Delay(120);
            Require(values.IsEmpty, "Configured local interval must hold incoming updates instead of publishing immediately.");
            await Until(() => values.Count >= 1);
            Require(values.Count == 1 && values.Last().Reading.Snapshot.FiveHour?.RemainingPercent == 87,
                "Several quota events in one local interval must produce only the newest snapshot.");
            var firstTick = values.Last().Tick;
            await File.AppendAllTextAsync(file, Record(DateTimeOffset.UtcNow, 14) + "\n" + Record(DateTimeOffset.UtcNow.AddMilliseconds(1), 15) + "\n");
            await Task.Delay(120); Require(values.Count == 1, "A second file notification must not bypass the interval.");
            await Until(() => values.Count >= 2);
            Require(values.Count == 2 && values.Last().Tick - firstTick >= 350 && values.Last().Reading.Snapshot.FiveHour?.RemainingPercent == 85,
                "Local batches must remain separated by the selected interval.");
            var generation = source.Generation;
            source.IntervalMilliseconds = 2000;
            await File.AppendAllTextAsync(file, Record(DateTimeOffset.UtcNow, 16) + "\n");
            await Task.Delay(150); Require(values.Count == 2, "Changing to a longer interval must take effect without restarting.");
            var timer = System.Diagnostics.Stopwatch.StartNew(); source.IntervalMilliseconds = 0;
            await Until(() => values.Count >= 3);
            Require(timer.Elapsed < TimeSpan.FromSeconds(1) && source.Generation == generation, "Returning to immediate mode must wake the same subscription promptly.");
            source.IntervalMilliseconds = 3_600_000;
            timer.Restart(); await source.DisposeAsync();
            Require(timer.Elapsed < TimeSpan.FromSeconds(1), "A long local interval must not delay shutdown.");
            foreach (var value in new[] { -1, 1, 99, int.MaxValue })
                Require(new CompanionSettings { LocalQuotaSyncIntervalMs = value }.Normalize().LocalQuotaSyncIntervalMs == 0, "Invalid persisted local cadence must recover to immediate mode.");
            foreach (var value in new[] { 0, 100, 500, 1000, 5000, 3_600_000 })
                Require(new CompanionSettings { LocalQuotaSyncIntervalMs = value }.Normalize().LocalQuotaSyncIntervalMs == value, "A valid local interval must survive normalization.");
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new() { LocalQuotaSyncIntervalMs = 5000, QuotaNetworkMode = "local", RefreshIntervalSeconds = 60 });
            Require(store.Load().LocalQuotaSyncIntervalMs == 5000 && store.Load().RefreshIntervalSeconds == 60 && store.Load().QuotaNetworkMode == "local",
                "Local interval persistence must remain independent of online preferences.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS adjustable local cadence, newest-event coalescing, no early publication, live changes, immediate wakeup, prompt shutdown and independent persistence");
    }
}
