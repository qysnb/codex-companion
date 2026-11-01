using System.Text.Json;
using CodexCompanion.Core;

public static class LocalQuotaServiceTests
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static QuotaSnapshot Snapshot(DateTimeOffset time, double remaining) => new(QuotaStatus.Live,
        new(remaining, 300, time.AddHours(2)), new(80, 10080, time.AddDays(4)), time);
    static async Task RunAsync()
    {
        Require(new CompanionSettings().LocalQuotaSync && !new CompanionSettings { LocalQuotaSync = false }.Normalize().LocalQuotaSync,
            "Local sync must default on and preserve an explicit opt-out.");
        var now = DateTimeOffset.UtcNow; var connects = 0; var notices = 0;
        await using var service = new QuotaService(_ => { connects++; return Task.FromResult<ICodexConnection>(new Fake()); }, () => now)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        service.ResetDetected += _ => notices++;
        await service.StartAsync();
        Require(service.AcceptLocalReading(new(Snapshot(now, 0), 100, now)), "A local host reading must enter the quota cache without connecting.");
        Require(service.Source == "local-session" && service.Snapshot.FiveHour?.RemainingPercent == 0, "Source provenance and quota must be visible.");
        now = now.AddSeconds(1);
        service.AcceptLocalReading(new(Snapshot(now, 100), 100, now));
        await service.RefreshAsync();
        Require(connects == 0 && notices == 0, "Unverified local identities must not cause network reads or account-verified reset notices.");
        Require(!service.AcceptLocalReading(new(Snapshot(now.AddHours(-1), 99), 100, now)), "Old host events must not replace newer quota.");
        service.InvalidateLocalReading(101, true);
        Require(service.Source == "none" && service.Snapshot.FiveHour is null, "Auth metadata changes must clear old cache.");
        Require(!service.AcceptLocalReading(new(Snapshot(now, 33), 100, now)), "Events from an invalidated generation must be ignored.");
        service.AcceptLocalReading(new(Snapshot(now, 66), 101, now));
        now = now.AddSeconds(1);
        await service.RefreshAsync(true);
        Require(connects == 1 && service.Source == "app-server" && service.Snapshot.FiveHour?.RemainingPercent == 70,
            "An explicit newer verified read must remain available and must not merge sources.");
        now = now.AddSeconds(1);
        service.AcceptLocalReading(new(Snapshot(now, 65) with { Weekly = null }, 101, now));
        Require(service.Source == "local-session" && service.Snapshot.Weekly is null, "A missing local window must remain unknown, not borrow another account's value.");
        service.LocalSyncEnabled = false;
        Require(!service.AcceptLocalReading(new(Snapshot(now, 60), 102, now)) && service.Source == "app-server", "Disabling subscription must ignore late callbacks and expose the independent cache.");

        var pending = new TaskCompletionSource<ICodexConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var race = new QuotaService(_ => pending.Task, () => now) { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        var read = race.RefreshAsync(true);
        await Task.Delay(20);
        race.AcceptLocalReading(new(Snapshot(now.AddSeconds(1), 52), 200, now));
        pending.SetResult(new Fake());
        await read;
        Require(race.Source == "local-session" && race.Snapshot.FiveHour?.RemainingPercent == 52,
            "An older pending network read must not overwrite a newer local event.");

        var onDemandCalls = 0;
        await using var demand = new QuotaService(_ => { onDemandCalls++; return Task.FromResult<ICodexConnection>(new Fake()); }, () => now)
            { NetworkMode = QuotaNetworkMode.OnDemand, LocalSyncEnabled = true, RefreshInterval = TimeSpan.FromMinutes(15) };
        demand.AcceptLocalReading(new(Snapshot(now, 51), 300, now));
        await demand.RefreshAsync();
        Require(onDemandCalls == 0, "Fresh subscribed data must satisfy on-demand clicks without another online read.");
        demand.AcceptLocalReading(new(Snapshot(now.AddMinutes(-30), 40), 301, now));
        await demand.RefreshAsync();
        Require(onDemandCalls == 1, "A delayed old event must not masquerade as fresh data and suppress a permitted supplement.");
        var automaticCalls = 0;
        await using var automatic = new QuotaService(_ => { automaticCalls++; return Task.FromResult<ICodexConnection>(new Fake()); }, () => now)
            { NetworkMode = QuotaNetworkMode.Automatic, LocalSyncEnabled = true };
        automatic.AcceptLocalReading(new(Snapshot(now, 50), 400, now));
        await automatic.StartAsync(); await Task.Delay(100);
        Require(automaticCalls == 0, "A fresh local event must suppress redundant automatic startup reads.");
        Console.WriteLine("PASS local quota cache overlay, source provenance, zero-query clicks, generation/account isolation, whole-source snapshots, manual reads and late-response ordering");
    }
    sealed class Fake : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification { add { } remove { } }
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.FromResult(JsonSerializer.SerializeToElement(method == "account/read"
                ? (object)new { account = new { type = "chatgpt", id = "unit-test-account" } }
                : new { rateLimits = new { primary = new { usedPercent = 30, windowDurationMins = 300, resetsAt = 2000000000 } } }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
