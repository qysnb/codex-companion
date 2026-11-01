using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaNetworkPolicyTests
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        SettingsMigration();
        await LocalDoesNotConnectWithoutConsent();
        await ExplicitManualReadCoalesces();
        await LocalFailureDoesNotRetry();
        await LocalNotificationsAndResetsCannotRefetch();
        await OnDemandUsesAttemptBudget();
        await AutomaticRemainsAvailable();
        await SwitchingLocalCancelsAutomaticRead();
        await SwitchingPolicyKeepsExplicitManualRead();
        await FreshPushSurvivesPolicyCancellation();
        await ManualRequestsWaitForCancelledReadCleanup();
        Console.WriteLine("PASS local quota zero-network default/migration, explicit refresh coalescing, no hidden retries or notification/reset bypass, on-demand attempt budget and opt-in automatic mode");
    }

    static void SettingsMigration()
    {
        Require(new CompanionSettings().QuotaNetworkMode == "local", "New companion settings must default to local-only quota display.");
        foreach (var value in new[] { "local", "on-demand", "automatic" })
            Require(new CompanionSettings { QuotaNetworkMode = value }.Normalize().QuotaNetworkMode == value, "Explicit network mode must survive normalization.");
        foreach (var value in new string?[] { null, "", "remote", "Automatic" })
            Require(new CompanionSettings { QuotaNetworkMode = value! }.Normalize().QuotaNetworkMode == "local", "Invalid network mode must recover to local, not silently enable polling.");
        var directory = Path.Combine(Path.GetTempPath(), "companion-network-policy-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            var store = new SettingsStore(path);
            File.WriteAllText(path, "{\"RefreshIntervalSeconds\":60,\"Character\":\"flash\",\"BubbleDurationSeconds\":5}");
            var migrated = store.Load();
            Require(migrated.LocalQuotaSyncIntervalMs == 0, "Older settings must preserve event-driven immediate local sync.");
            Require(migrated.QuotaNetworkMode == "local" && migrated.RefreshIntervalSeconds == 60 && migrated.Character == "flash" && migrated.BubbleDurationSeconds == 5,
                "Older settings must migrate to local mode without discarding unrelated choices.");
            store.Save(migrated with { QuotaNetworkMode = "on-demand" });
            Require(store.Load().QuotaNetworkMode == "on-demand", "A deliberately selected network mode must persist.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    static async Task LocalDoesNotConnectWithoutConsent()
    {
        var clock = new Clock(); var connections = 0;
        await using var service = new QuotaService(_ => { Interlocked.Increment(ref connections); return Task.FromResult<ICodexConnection>(new Connection()); }, clock.Now)
        { NetworkMode = QuotaNetworkMode.Local, RefreshInterval = TimeSpan.FromSeconds(5) };
        await service.StartAsync();
        foreach (var visible in new[] { true, false, true, false })
        {
            service.IsVisible = visible; clock.Advance(86400);
            await service.StartAsync();
            var snapshot = await service.RefreshAsync();
            Require(snapshot.FiveHour is null, "An empty local cache must not fabricate quota.");
        }
        await Task.Delay(180);
        Require(connections == 0, "Local startup, visibility changes, elapsed intervals and normal clicks must never create a connection.");
    }

    static async Task ExplicitManualReadCoalesces()
    {
        var clock = new Clock(); var connections = 0;
        var connection = new Connection { QuotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new QuotaService(_ => { Interlocked.Increment(ref connections); return Task.FromResult<ICodexConnection>(connection); }, clock.Now)
        { NetworkMode = QuotaNetworkMode.Local, IsVisible = true };
        await service.StartAsync();
        var a = service.RefreshAsync(true);
        await Until(() => connection.QuotaReads == 1);
        var b = service.RefreshAsync(true);
        connection.QuotaGate.SetResult();
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(3));
        Require(connections == 1 && connection.AccountReads == 1 && connection.QuotaReads == 1 && results.All(s => s.Status == QuotaStatus.Live),
            "Explicit manual refreshes must use one authenticated, coalesced read even in local mode.");
        clock.Advance(86400); await service.StartAsync();
        await service.RefreshAsync(); await Task.Delay(150);
        Require(connection.QuotaReads == 1, "A previously warmed local cache must not begin automatic polling.");
    }

    static async Task LocalFailureDoesNotRetry()
    {
        var clock = new Clock(); var reads = new Connection { FailQuota = true }; var connects = 0;
        await using var service = new QuotaService(_ => { Interlocked.Increment(ref connects); return Task.FromResult<ICodexConnection>(reads); }, clock.Now)
        { NetworkMode = QuotaNetworkMode.Local, IsVisible = true };
        await service.StartAsync();
        var failed = await service.RefreshAsync(true).WaitAsync(TimeSpan.FromSeconds(3));
        Require(failed.Status == QuotaStatus.Unavailable, "Manual network failure must remain an explicit unavailable result.");
        foreach (var seconds in new[] { 5, 15, 60, 3600 })
        {
            clock.Advance(seconds); await service.StartAsync(); await service.RefreshAsync(); await Task.Delay(60);
        }
        Require(connects == 1 && reads.QuotaReads == 1, "Local read failure must not silently start 5/15/60-second retries or a periodic refresh.");
    }

    static async Task LocalNotificationsAndResetsCannotRefetch()
    {
        var clock = new Clock(); var connection = new Connection { Reset = clock.Now().AddSeconds(1) };
        var connects = 0;
        await using var service = new QuotaService(_ => { Interlocked.Increment(ref connects); return Task.FromResult<ICodexConnection>(connection); }, clock.Now)
        { NetworkMode = QuotaNetworkMode.Local, IsVisible = true };
        await service.StartAsync();
        await service.RefreshAsync(true).WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(2); await service.StartAsync();
        connection.Notify("account/rateLimits/updated", JsonSerializer.SerializeToElement(new { rateLimits = new { limitId = "codex", primary = new { usedPercent = 30, windowDurationMins = 300 } } }));
        await Task.Delay(180);
        Require(connects == 1 && connection.QuotaReads == 1, "An elapsed cached reset and a sparse quota notification must not bypass local network policy.");
        connection.Notify("account/updated", JsonSerializer.SerializeToElement(new { authMode = "chatgpt" }));
        Require(service.Snapshot.FiveHour is null && service.Snapshot.Weekly is null, "Local policy must still clear quota on an account change.");
        await Task.Delay(180);
        Require(connection.AccountReads == 1 && connection.QuotaReads == 1, "An account notification must not silently refetch in local mode.");
    }

    static async Task OnDemandUsesAttemptBudget()
    {
        foreach (var fail in new[] { false, true })
        {
            var clock = new Clock(); var connections = new List<Connection>();
            await using var service = new QuotaService(_ =>
            {
                var connection = new Connection { FailQuota = fail };
                lock (connections) connections.Add(connection);
                return Task.FromResult<ICodexConnection>(connection);
            }, clock.Now) { NetworkMode = QuotaNetworkMode.OnDemand, RefreshInterval = TimeSpan.FromSeconds(5), IsVisible = true };
            await service.StartAsync(); await Task.Delay(80);
            Require(connections.Count == 0, "On-demand startup must not prefetch.");
            await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Require(connections.Sum(c => c.QuotaReads) == 1, "A deliberate first quota click must be able to read on demand.");
            clock.Advance(899); await service.StartAsync();
            await service.RefreshAsync(); await Task.Delay(160);
            Require(connections.Sum(c => c.QuotaReads) == 1, "Repeated clicks must respect the 900-second attempt budget, including failed reads.");
            clock.Advance(1); await service.StartAsync(); await Task.Delay(80);
            Require(connections.Sum(c => c.QuotaReads) == 1, "An elapsed on-demand deadline alone must not trigger background reads.");
            await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Require(connections.Sum(c => c.QuotaReads) == 2, "The first deliberate click at the budget boundary must refresh once.");
        }
    }

    static async Task AutomaticRemainsAvailable()
    {
        var clock = new Clock(); var reads = new Connection();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(reads), clock.Now)
        { NetworkMode = QuotaNetworkMode.Automatic, IsVisible = true, RefreshInterval = TimeSpan.FromSeconds(5) };
        await service.StartAsync(); await Until(() => reads.QuotaReads == 1 && service.Snapshot.Status == QuotaStatus.Live);
        clock.Advance(5); await service.StartAsync();
        await Until(() => reads.QuotaReads == 2);
        service.NetworkMode = QuotaNetworkMode.Local;
        clock.Advance(86400); await service.StartAsync(); await Task.Delay(150);
        Require(reads.QuotaReads == 2, "Switching from opt-in automatic to local must stop future polling.");
    }

    static async Task SwitchingLocalCancelsAutomaticRead()
    {
        var clock = new Clock(); var connects = 0;
        var connection = new Connection { QuotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new QuotaService(_ => { Interlocked.Increment(ref connects); return Task.FromResult<ICodexConnection>(connection); }, clock.Now)
        { NetworkMode = QuotaNetworkMode.Automatic, IsVisible = true };
        await service.StartAsync();
        await Until(() => connection.QuotaReads == 1);
        var reading = service.RefreshAsync();
        service.NetworkMode = QuotaNetworkMode.Local;
        var stopped = await reading.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => connection.Disposed && !service.IsRefreshing);
        Require(connection.Cancelled && stopped.FailureKind == QuotaFailureKind.None && stopped.RetryAt is null && stopped.Error is null,
            "Selecting local mode must cancel and release an automatic read without reporting a network failure or scheduling backoff.");
        clock.Advance(86400); await service.StartAsync(); await Task.Delay(150);
        Require(connects == 1 && connection.QuotaReads == 1, "A policy-cancelled automatic read must not retry in local mode.");
    }

    static async Task SwitchingPolicyKeepsExplicitManualRead()
    {
        var clock = new Clock();
        var connection = new Connection { QuotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), clock.Now)
        { NetworkMode = QuotaNetworkMode.Local, IsVisible = true };
        await service.StartAsync();
        var reading = service.RefreshAsync(true);
        await Until(() => connection.QuotaReads == 1);
        service.NetworkMode = QuotaNetworkMode.OnDemand;
        service.NetworkMode = QuotaNetworkMode.Local;
        await Task.Delay(80);
        Require(!reading.IsCompleted && !connection.Cancelled && !connection.Disposed,
            "A deliberate manual refresh must not be cancelled by changing the automatic network policy.");
        connection.QuotaGate.SetResult();
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Require(result.Status == QuotaStatus.Live && result.FailureKind == QuotaFailureKind.None && connection.QuotaReads == 1,
            "The retained explicit manual read must complete and publish its verified quota once.");
    }

    static async Task FreshPushSurvivesPolicyCancellation()
    {
        var clock = new Clock(); var connection = new Connection();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), clock.Now)
        { NetworkMode = QuotaNetworkMode.Automatic, IsVisible = true };
        await service.RefreshAsync(true).WaitAsync(TimeSpan.FromSeconds(3));
        connection.AccountGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Advance(31);
        var olderRead = service.RefreshAsync();
        try
        {
            await Until(() => connection.AccountReads == 2);
            clock.Advance(1);
            connection.Notify("account/rateLimits/updated", JsonSerializer.SerializeToElement(new { rateLimits = new { limitId = "codex",
                primary = new { usedPercent = 10, windowDurationMins = 300 }, secondary = new { usedPercent = 40, windowDurationMins = 10080 } } }));
            var pushed = service.Snapshot;
            Require(pushed.Status == QuotaStatus.Live && pushed.FiveHour?.RemainingPercent == 90 && pushed.UpdatedAt == clock.Now(),
                "An authenticated push must update the quota while an older account verification is waiting.");
            service.NetworkMode = QuotaNetworkMode.Local;
            var result = await olderRead.WaitAsync(TimeSpan.FromSeconds(3));
            Require(connection.Cancelled && connection.Disposed && result == pushed && service.Snapshot == pushed,
                "Policy cancellation of an older unverified read must not clear a newer authenticated live quota push.");
            Require(result.FailureKind == QuotaFailureKind.None && result.Error is null && result.RetryAt is null,
                "Preserving a newer live push after policy cancellation must retain a healthy error state.");
        }
        finally { connection.AccountGate.TrySetResult(); }
    }

    static async Task ManualRequestsWaitForCancelledReadCleanup()
    {
        var clock = new Clock(); var connects = 0;
        var previous = new Connection
        {
            QuotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
            DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var replacement = new Connection { QuotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(Interlocked.Increment(ref connects) == 1 ? previous : replacement), clock.Now)
        { NetworkMode = QuotaNetworkMode.Automatic, IsVisible = true };
        try
        {
            await service.StartAsync();
            await Until(() => previous.QuotaReads == 1);
            service.NetworkMode = QuotaNetworkMode.Local;
            await Until(() => previous.DisposalStarted);
            var a = service.RefreshAsync(true);
            var b = service.RefreshAsync(true);
            await Task.Delay(60);
            Require(connects == 1 && !a.IsCompleted && !b.IsCompleted,
                "Manual refreshes arriving during policy-cancelled cleanup must wait instead of inheriting the cancelled result or reusing its connection.");
            previous.DisposeGate.SetResult();
            await Until(() => replacement.QuotaReads == 1);
            await Task.Delay(60);
            Require(connects == 2 && replacement.AccountReads == 1 && replacement.QuotaReads == 1,
                "Concurrent manual refreshes after cancelled cleanup must coalesce into one replacement authenticated read.");
            replacement.QuotaGate.SetResult();
            var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(3));
            Require(previous.Disposed && previous.Cancelled && results.All(value => value.Status == QuotaStatus.Live && value.FiveHour?.RemainingPercent == 75) &&
                results.All(value => value.FailureKind == QuotaFailureKind.None && value.RetryAt is null),
                "Both manual callers must receive the replacement quota rather than lose their request to the previous policy cancellation.");
        }
        finally
        {
            previous.DisposeGate.TrySetResult();
            previous.QuotaGate.TrySetResult();
            replacement.QuotaGate.TrySetResult();
        }
    }

    static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("Quota network policy test deadline.");
            await Task.Delay(5);
        }
    }
    sealed class Clock
    {
        long ticks = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public DateTimeOffset Now() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    sealed class Connection : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification;
        public int AccountReads, QuotaReads;
        public bool FailQuota, Cancelled, Disposed, DisposalStarted;
        public DateTimeOffset? Reset;
        public TaskCompletionSource? QuotaGate, AccountGate, DisposeGate;
        public void Notify(string method, JsonElement payload) => Notification?.Invoke(method, payload);
        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            if (method == "account/read")
            {
                Interlocked.Increment(ref AccountReads);
                if (AccountGate is not null)
                {
                    try { await AccountGate.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancelled = true; throw; }
                }
                return JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", id = "network-policy-test-account" } });
            }
            Interlocked.Increment(ref QuotaReads);
            if (QuotaGate is not null)
            {
                try { await QuotaGate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancelled = true; throw; }
            }
            if (FailQuota) throw new IOException("Synthetic quota failure.");
            return JsonSerializer.SerializeToElement(new { rateLimits = new { limitId = "codex",
                primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = Reset?.ToUnixTimeSeconds() },
                secondary = new { usedPercent = 40, windowDurationMins = 10080 } } },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }
        public async ValueTask DisposeAsync()
        {
            DisposalStarted = true;
            if (DisposeGate is not null) await DisposeGate.Task;
            Disposed = true;
        }
    }
}
