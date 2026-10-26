using System.Diagnostics;
using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaTests
{
    static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var parsed = QuotaParser.Parse(Json("""
            {"rateLimits":{"limitId":"other","primary":{"usedPercent":1,"windowDurationMins":300}},
             "rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1793000000},"secondary":{"usedPercent":25.5,"windowDurationMins":300}}}}
            """), now);
        Check(parsed.FiveHour?.RemainingPercent == 74.5, "Five-hour window must be identified by duration.");
        Check(parsed.Weekly?.RemainingPercent == 60, "Codex bucket must win and weekly duration must be recognized.");
        Check(parsed.FiveHour!.ResetsAt is null, "Missing reset timestamp must stay unknown.");
        Check(parsed.Weekly!.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1793000000), "Reset is Unix seconds.");
        Check(QuotaParser.Parse(Json("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":2,"windowDurationMins":300}}}"""), now).FiveHour is null, "Other buckets must not become Codex quota.");
        Check(QuotaParser.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":15}}}"""), now).FiveHour is null, "Unknown duration must remain unknown.");
        Check(QuotaParser.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":120,"windowDurationMins":300}}}"""), now).FiveHour?.RemainingPercent == 0, "Percentage must be clamped.");
        Check(QuotaParser.Parse(Json("""{"rateLimits":{"primary":{"windowDurationMins":300}}}"""), now).FiveHour is null, "Missing usage must not fabricate 100%.");
        Check(QuotaParser.Parse(Json("""{"rateLimitsByLimitId":{"other":{}},"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}}}"""), now).FiveHour is null, "An explicit multi-bucket response without Codex must not use ambiguous legacy fallback.");
        ServiceTests(now).GetAwaiter().GetResult();
        StartupPrefetchTests(now).GetAwaiter().GetResult();
        StartupCoalescingTest(now).GetAwaiter().GetResult();
        ColdRefreshSchedulingTest(now).GetAwaiter().GetResult();
        CacheDuringDisconnectTest(now).GetAwaiter().GetResult();
        InitialAccountNotificationTest(now).GetAwaiter().GetResult();
        AccountIdentityTests(now).GetAwaiter().GetResult();
        CadenceAndBackoffTests(now).GetAwaiter().GetResult();
        ConfigurableSchedulerTests(now).GetAwaiter().GetResult();
        ScheduledRefreshRaceTest(now).GetAwaiter().GetResult();
        ResetSchedulerTests(now).GetAwaiter().GetResult();
        Console.WriteLine("PASS quota parser, cache, stale state, account switch, coalescing");
    }
    static async Task ServiceTests(DateTimeOffset now)
    {
        var connection = new FakeConnection();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now);
        var first = await service.RefreshAsync();
        Check(first.Status == QuotaStatus.Live && first.FiveHour?.RemainingPercent == 75, "Refresh must fetch authenticated quota.");
        await service.RefreshAsync();
        Check(connection.Reads == 1, "Repeated clicks within 30 seconds must use cache.");
        connection.Fail = true;
        var stale = await service.RefreshAsync(true);
        Check(stale.Status == QuotaStatus.Stale && stale.FiveHour?.RemainingPercent == 75, "Failed refresh retains last real quota.");
        Check(stale.Error is not null && !stale.Error.Contains("SECRET"), "Remote error details must not leak.");
        connection.Notify("account/updated", Json("""{"authMode":null}"""));
        Check(service.Snapshot.FiveHour is null, "Account switch immediately clears old quota.");

        var delayed = new FakeConnection { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coalesced = new QuotaService(_ => Task.FromResult<ICodexConnection>(delayed), () => now);
        var a = coalesced.RefreshAsync(true);
        var b = coalesced.RefreshAsync(true);
        delayed.Gate.SetResult();
        await Task.WhenAll(a, b);
        Check(delayed.Reads == 1, "Concurrent requests must coalesce.");
        delayed.Notify("account/rateLimits/updated", Json("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":55,"windowDurationMins":300}}}"""));
        Check(coalesced.Snapshot.FiveHour?.RemainingPercent == 45, "Push updates must update cached quota.");

        var signedOut = new FakeConnection { SignedOut = true };
        await using var noAccount = new QuotaService(_ => Task.FromResult<ICodexConnection>(signedOut));
        Check((await noAccount.RefreshAsync()).Status == QuotaStatus.NotLoggedIn && signedOut.Reads == 0, "Signed-out accounts must not request limits.");

    }

    static async Task WaitUntil(Func<bool> condition, string message)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Check(condition(), message);
    }

    static async Task StartupPrefetchTests(DateTimeOffset now)
    {
        var connection = new FakeConnection();
        using var connectGate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        await using var service = new QuotaService(_ =>
        {
            Interlocked.Increment(ref connections);
            entered.TrySetResult();
            connectGate.Wait(TimeSpan.FromSeconds(2)); // Even synchronous transport setup must run off the UI thread.
            return Task.FromResult<ICodexConnection>(connection);
        }, () => now);
        var watch = Stopwatch.StartNew();
        await service.StartAsync();
        var startupElapsed = watch.Elapsed;
        Check(startupElapsed < TimeSpan.FromMilliseconds(150), "Startup must schedule prefetch without waiting for transport setup.");
        await WaitUntil(() => entered.Task.IsCompleted, "StartAsync must prefetch before the first quota click.");
        watch.Restart();
        var loading = service.Snapshot;
        var snapshotElapsed = watch.Elapsed;
        Check(snapshotElapsed < TimeSpan.FromMilliseconds(150) && loading.Status == QuotaStatus.Loading,
            "A cold snapshot must return immediately while transport is gated.");
        service.IsVisible = true;
        connectGate.Set();
        await WaitUntil(() => service.Snapshot.Status == QuotaStatus.Live, "Startup prefetch must populate the memory snapshot.");
        Check((await service.RefreshAsync()).FiveHour?.RemainingPercent == 75 && connection.Reads == 1,
            "The first warmed click must reuse startup quota without another RPC.");

        connection.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = service.RefreshAsync(true);
        await WaitUntil(() => connection.Reads == 2, "Forced refresh must enter the gated quota request.");
        watch.Restart();
        var cached = service.RefreshAsync();
        var cacheElapsed = watch.Elapsed;
        Check(cached.IsCompleted && (await cached).FiveHour?.RemainingPercent == 75 && cacheElapsed < TimeSpan.FromMilliseconds(150),
            "A fresh cached click must not wait for an unrelated in-flight refresh.");
        Check(connection.Reads == 2 && connections == 1, "Cached clicks must not add quota RPCs or connections.");
        connection.Gate.SetResult();
        await refreshing;

        service.IsVisible = false;
        await WaitUntil(() => connection.Disposed, "Hidden state must release its own idle quota connection.");
        Check(service.Snapshot.FiveHour?.RemainingPercent == 75, "Releasing a process must preserve the last actual reading.");
        Console.WriteLine($"PASS startup prefetch {startupElapsed.TotalMilliseconds:F2}ms, memory {snapshotElapsed.TotalMilliseconds:F2}ms, cache {cacheElapsed.TotalMilliseconds:F2}ms, hidden disconnect");
    }

    static async Task InitialAccountNotificationTest(DateTimeOffset now)
    {
        var connection = new FakeConnection { NotifyOnAccountRead = true };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now);
        var snapshot = await service.RefreshAsync();
        Check(snapshot.Status == QuotaStatus.Live && connection.AccountReads == 1 && connection.Reads == 1,
            "The initial account notification must not discard account/read and repeat the quota RPC chain.");
        await Task.Delay(150);
        Check(connection.AccountReads == 1 && connection.Reads == 1, "The initial notification must not schedule a redundant forced refetch.");

        connection.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = service.RefreshAsync(true);
        await WaitUntil(() => connection.Reads == 2, "The obsolete account quota request must be in flight.");
        connection.Notify("account/updated", Json("""{"authMode":"chatgpt"}"""));
        Check(service.Snapshot.FiveHour is null, "A real account change must immediately clear old quota even during a read.");
        var previousGate = connection.Gate;
        connection.Gate = null;
        connection.Email = "changed@example.test";
        connection.UsedPercent = 70;
        previousGate.SetResult();
        await oldRead;
        await WaitUntil(() => service.Snapshot.FiveHour?.RemainingPercent == 30, "An invalidated read must refetch the new account quota.");
        Console.WriteLine("PASS initial account notification race and in-flight account change safety");
    }

    static async Task ColdRefreshSchedulingTest(DateTimeOffset now)
    {
        using var gate = new ManualResetEventSlim();
        var connection = new FakeConnection();
        await using var service = new QuotaService(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            return Task.FromResult<ICodexConnection>(connection);
        }, () => now);
        var watch = Stopwatch.StartNew();
        var refresh = service.RefreshAsync();
        var elapsed = watch.Elapsed;
        gate.Set();
        await refresh;
        Check(elapsed < TimeSpan.FromMilliseconds(150), "A cold click must queue transport work without synchronously blocking the caller.");
        Console.WriteLine($"PASS cold refresh queues transport in {elapsed.TotalMilliseconds:F2}ms");
    }

    static async Task StartupCoalescingTest(DateTimeOffset now)
    {
        var connection = new FakeConnection { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now);
        service.IsVisible = true;
        var first = service.RefreshAsync();
        await WaitUntil(() => connection.Reads == 1, "An explicit first quota request must be gated before startup.");
        await service.StartAsync();
        connection.Gate.SetResult();
        await first;
        await Task.Delay(100);
        Check(connection.Reads == 1, "Startup during an existing first read must coalesce without an extra prefetch afterward.");
    }

    static async Task CacheDuringDisconnectTest(DateTimeOffset now)
    {
        var clock = now;
        var connection = new FakeConnection();
        var retryConnection = new FakeConnection();
        var connects = 0;
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(Interlocked.Increment(ref connects) == 1 ? connection : retryConnection), () => clock);
        await service.RefreshAsync();
        clock = now.AddSeconds(1);
        connection.Fail = true;
        connection.Connected = false;
        connection.DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = service.RefreshAsync(true);
        await WaitUntil(() => connection.DisposalStarted, "The failed connection must reach its gated disposal.");
        try
        {
            Check((await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2))).Status == QuotaStatus.Stale,
                "A cached click during failed transport cleanup must remain immediate and correctly marked stale.");
        }
        finally { connection.DisposeGate.SetResult(); }
        await failed;
        clock = now.AddSeconds(6);
        Check((await service.RefreshAsync()).Status == QuotaStatus.Live && retryConnection.Reads == 1,
            "A cached click during transport cleanup must not overwrite the 5-second failure retry deadline.");
        Console.WriteLine("PASS cached click during disconnect preserves the failure retry deadline");
    }

    static async Task AccountIdentityTests(DateTimeOffset now)
    {
        var connection = new FakeConnection();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now);
        await service.RefreshAsync();
        connection.Email = "another@example.test";
        connection.Fail = true;
        connection.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = service.RefreshAsync(true);
        await WaitUntil(() => connection.Reads == 2, "The new account quota request must start.");
        Check(service.Snapshot.FiveHour is null, "account/read identity changes must clear old quota before waiting for new limits.");
        connection.Gate.SetResult();
        Check((await changed).Status == QuotaStatus.Unavailable && service.Snapshot.FiveHour is null,
            "A failed read for a new account must never retain the previous account quota as stale.");

        var unknown = new FakeConnection { Email = null };
        await using var unknownService = new QuotaService(_ => Task.FromResult<ICodexConnection>(unknown), () => now);
        await unknownService.RefreshAsync();
        unknown.Fail = true;
        Check((await unknownService.RefreshAsync(true)).FiveHour is null,
            "An account without a comparable identity must not reuse an old account snapshot after a failed read.");

        var clock = now;
        var oldConnection = new FakeConnection();
        var nextConnection = new FakeConnection { AccountGate = new(TaskCreationOptions.RunContinuationsAsynchronously), Fail = true, NotifyOnAccountRead = true };
        var connects = 0;
        await using var reconnect = new QuotaService(_ => Task.FromResult<ICodexConnection>(Interlocked.Increment(ref connects) == 1 ? oldConnection : nextConnection), () => clock);
        await reconnect.RefreshAsync();
        await reconnect.StartAsync();
        await WaitUntil(() => oldConnection.Disposed, "A hidden service must release its old transport before reconnecting.");
        clock = now.AddSeconds(31);
        var verifying = reconnect.RefreshAsync();
        await WaitUntil(() => nextConnection.AccountReads == 1, "Reconnect must verify the account before reusing cached quota.");
        Check(reconnect.Snapshot.Status == QuotaStatus.Stale && reconnect.Snapshot.FiveHour?.RemainingPercent == 75,
            "A reconnect may retain real readings only as stale while the account identity is pending.");
        nextConnection.AccountGate.SetResult();
        Check((await verifying).Status == QuotaStatus.Stale && reconnect.Snapshot.FiveHour?.RemainingPercent == 75,
            "A verified same-account quota failure must preserve its real cached reading.");

        var beforeIdentity = new FakeConnection { AccountGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var unverified = new QuotaService(_ => Task.FromResult<ICodexConnection>(beforeIdentity), () => now);
        var pendingIdentity = unverified.RefreshAsync();
        await WaitUntil(() => beforeIdentity.AccountReads == 1, "Account identity lookup must be pending for notification isolation.");
        beforeIdentity.Notify("account/rateLimits/updated", Json("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":5,"windowDurationMins":300}}}"""));
        Check(unverified.Snapshot.Status == QuotaStatus.Loading && unverified.Snapshot.FiveHour is null,
            "Quota pushes received before account verification must not become a live account snapshot.");
        beforeIdentity.AccountGate.SetResult();
        await pendingIdentity;
        Console.WriteLine("PASS account identity verification and unknown account cache isolation");
    }

    static async Task CadenceAndBackoffTests(DateTimeOffset now)
    {
        var clock = now;
        var connection = new FakeConnection();
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => clock);
        service.RefreshInterval = TimeSpan.FromSeconds(60);
        service.IsVisible = true;
        await service.StartAsync();
        await WaitUntil(() => service.Snapshot.Status == QuotaStatus.Live, "Prefetch must succeed before cadence checks.");
        clock = now.AddSeconds(9);
        await service.StartAsync();
        await Task.Delay(100);
        Check(!connection.Disposed, "An active visible connection must remain available until 10 idle seconds.");
        clock = now.AddSeconds(10);
        await service.StartAsync();
        await WaitUntil(() => connection.Disposed, "An idle private quota connection must be released after 10 seconds.");
        Check(service.Snapshot.Status == QuotaStatus.Live && connection.Reads == 1, "Idle cleanup must retain the memory cache without another RPC.");
        clock = now.AddSeconds(31);
        service.IsVisible = false;
        service.IsVisible = true;
        await Task.Delay(100);
        Check(connection.Reads == 1, "Visibility changes must not bypass the 60-second background refresh cadence.");
        clock = now.AddSeconds(60);
        await service.StartAsync();
        await WaitUntil(() => connection.Reads == 2, "Visible background quota must refresh once the 60-second interval elapses.");

        var failed = new FakeConnection { Fail = true };
        clock = now;
        await using var retry = new QuotaService(_ => Task.FromResult<ICodexConnection>(failed), () => clock);
        await retry.RefreshAsync();
        foreach (var (before, due, reads) in new[] { (4, 5, 1), (19, 20, 2), (79, 80, 3) })
        {
            clock = now.AddSeconds(before);
            Check((await retry.RefreshAsync()).Status == QuotaStatus.Unavailable && failed.Reads == reads,
                "Repeated clicks must honor the 5/15/60-second failure backoff.");
            clock = now.AddSeconds(due);
            await retry.RefreshAsync();
            Check(failed.Reads == reads + 1, "A retry must be permitted when its backoff expires.");
        }
        Console.WriteLine("PASS 10-second idle release, 60-second background cadence, 5/15/60-second backoff");
    }
    static async Task ConfigurableSchedulerTests(DateTimeOffset now)
    {
        foreach (var interval in new[] { 5, 15, 60 })
        {
            var clock = now;
            var connections = new List<FakeConnection>();
            var reads = 0;
            await using var service = new QuotaService(_ =>
            {
                var connection = new FakeConnection { OnRead = () => Interlocked.Increment(ref reads) };
                lock (connections) connections.Add(connection);
                return Task.FromResult<ICodexConnection>(connection);
            }, () => clock);
            Check(service.RefreshInterval == TimeSpan.FromSeconds(30), "Default automatic quota interval must be 30 seconds.");
            service.RefreshInterval = TimeSpan.FromSeconds(interval);
            await service.StartAsync();
            await WaitUntil(() => service.Snapshot.Status == QuotaStatus.Live, "A hidden service must prefetch quota.");
            await WaitUntil(() => { lock (connections) return connections.All(c => c.Disposed); }, "Hidden periodic reads must release their private transport.");
            clock = now.AddSeconds(interval - 1);
            await service.StartAsync();
            await Task.Delay(50);
            Check(reads == 1, "Hidden periodic quota must not refresh before its configured deadline.");
            clock = now.AddSeconds(interval);
            await service.StartAsync();
            await WaitUntil(() => reads == 2 && service.Snapshot.Status == QuotaStatus.Live, "Hidden periodic quota must bypass the 30-second UI cache at its configured deadline.");
            await WaitUntil(() => { lock (connections) return connections.All(c => c.Disposed); }, "Each completed hidden periodic read must dispose its own connection.");
        }

        var changingClock = now;
        var changing = new FakeConnection();
        await using var changingService = new QuotaService(_ => Task.FromResult<ICodexConnection>(changing), () => changingClock);
        changingService.IsVisible = true;
        changingService.RefreshInterval = TimeSpan.FromSeconds(60);
        await changingService.StartAsync();
        await WaitUntil(() => changingService.Snapshot.Status == QuotaStatus.Live, "Changing-interval test must warm its cache.");
        changingClock = now.AddSeconds(10);
        changingService.RefreshInterval = TimeSpan.FromSeconds(15);
        await Task.Delay(50);
        Check(changing.Reads == 1, "Changing the interval must anchor to the last success.");
        changingClock = now.AddSeconds(15);
        await changingService.StartAsync();
        await WaitUntil(() => changing.Reads == 2 && changingService.Snapshot.Status == QuotaStatus.Live, "A shortened interval must take effect without another click.");
        changingService.RefreshInterval = TimeSpan.FromSeconds(60);
        changingClock = now.AddSeconds(30);
        await changingService.StartAsync();
        await Task.Delay(50);
        Check(changing.Reads == 2, "Lengthening the interval must postpone the old periodic deadline.");

        changing.Fail = true;
        await changingService.RefreshAsync(true); // First failure: retry at t=35.
        changingClock = now.AddSeconds(35);
        await changingService.RefreshAsync(true); // Second failure: retry at t=50.
        changingClock = now.AddSeconds(36);
        changingService.RefreshInterval = TimeSpan.FromSeconds(5);
        await Task.Delay(50);
        Check(changing.Reads == 4, "Changing the interval must not override a pending 15-second retry backoff.");
        changingClock = now.AddSeconds(50);
        await changingService.StartAsync();
        await WaitUntil(() => changing.Reads == 5, "Background failures must retry when their independent backoff expires.");
        changingService.RefreshInterval = TimeSpan.Zero;
        Check(changingService.RefreshInterval == TimeSpan.FromSeconds(5), "Automatic quota intervals must clamp to at least 5 seconds.");
        changingService.RefreshInterval = TimeSpan.FromDays(1);
        Check(changingService.RefreshInterval == TimeSpan.FromHours(1), "Automatic quota intervals must clamp to at most 3600 seconds.");
        Console.WriteLine("PASS hidden periodic intervals 5/15/60s, interval changes, range clamp, independent retry backoff");
    }

    static async Task ResetSchedulerTests(DateTimeOffset now)
    {
        var clock = now;
        var reset = now.AddSeconds(10);
        var connection = new FakeConnection { Reset = reset };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => clock);
        service.RefreshInterval = TimeSpan.FromSeconds(60);
        await service.StartAsync();
        await WaitUntil(() => service.Snapshot.Status == QuotaStatus.Live, "Reset scheduling must start with a real cached timestamp.");
        clock = reset;
        await service.StartAsync();
        await WaitUntil(() => connection.Reads == 2, "A cached reset must trigger a read immediately after its timestamp even while hidden.");
        for (var i = 0; i < 3; i++) { await service.StartAsync(); await Task.Delay(30); }
        Check(connection.Reads == 2, "A repeatedly returned past reset timestamp must not cause a request flood.");

        var flightClock = now;
        var inFlight = new FakeConnection { Reset = reset };
        await using var flightService = new QuotaService(_ => Task.FromResult<ICodexConnection>(inFlight), () => flightClock);
        flightService.IsVisible = true;
        flightService.RefreshInterval = TimeSpan.FromSeconds(60);
        await flightService.StartAsync();
        await WaitUntil(() => flightService.Snapshot.Status == QuotaStatus.Live, "In-flight reset test must warm its cache.");
        flightClock = now.AddSeconds(9);
        inFlight.Reset = now.AddSeconds(100); // A pre-reset read may already return a future reset.
        inFlight.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeReset = flightService.RefreshAsync(true);
        await WaitUntil(() => inFlight.Reads == 2, "The pre-reset request must be in flight.");
        flightClock = reset;
        await flightService.StartAsync();
        var watch = Stopwatch.StartNew();
        var memory = flightService.Snapshot;
        Check(watch.Elapsed < TimeSpan.FromMilliseconds(150) && memory.FiveHour is not null, "Reset scheduling must leave snapshot reads immediate while transport is gated.");
        var oldGate = inFlight.Gate;
        inFlight.Gate = null;
        oldGate.SetResult();
        await beforeReset;
        await WaitUntil(() => inFlight.Reads == 3, "A reset during a pre-reset request must cause exactly one post-reset follow-up.");
        await flightService.StartAsync();
        await Task.Delay(50);
        Check(inFlight.Reads == 3, "A completed reset follow-up must not repeat for an already observed reset.");

        var simultaneousClock = now;
        var simultaneous = new FakeConnection { Reset = now.AddSeconds(15) };
        await using var sameDeadline = new QuotaService(_ => Task.FromResult<ICodexConnection>(simultaneous), () => simultaneousClock);
        sameDeadline.RefreshInterval = TimeSpan.FromSeconds(15);
        await sameDeadline.StartAsync();
        await WaitUntil(() => sameDeadline.Snapshot.Status == QuotaStatus.Live, "Combined-deadline test must warm its cache.");
        simultaneousClock = now.AddSeconds(15);
        await sameDeadline.StartAsync();
        await WaitUntil(() => simultaneous.Reads == 2, "A simultaneous reset and periodic deadline must refresh quota.");
        await sameDeadline.StartAsync();
        await Task.Delay(50);
        Check(simultaneous.Reads == 2, "Reset and periodic deadlines at the same instant must coalesce into one read.");
        Console.WriteLine("PASS hidden reset deadlines, in-flight post-reset follow-up, reset/periodic coalescing, no past-reset flood");
    }

    static async Task ScheduledRefreshRaceTest(DateTimeOffset now)
    {
        var clock = now;
        var oldConnection = new FakeConnection();
        var replacement = new FakeConnection();
        var connects = 0;
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(Interlocked.Increment(ref connects) == 1 ? oldConnection : replacement), () => clock);
        service.IsVisible = true;
        service.RefreshInterval = TimeSpan.FromSeconds(15);
        await service.StartAsync();
        await WaitUntil(() => service.Snapshot.Status == QuotaStatus.Live, "Scheduled refresh race test must warm its quota.");
        oldConnection.DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock = now.AddSeconds(15);
        await service.StartAsync();
        await WaitUntil(() => oldConnection.DisposalStarted, "The scheduler must reach its gated idle transport cleanup.");
        await service.RefreshAsync(true);
        oldConnection.DisposeGate.SetResult();
        await Task.Delay(100);
        Check(replacement.Reads == 1, "A scheduled decision delayed by old transport cleanup must not duplicate a newer successful manual refresh.");
        Console.WriteLine("PASS scheduled refresh rechecks its deadline after detached transport cleanup");
    }

    sealed class FakeConnection : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification;
        public int Reads, AccountReads;
        public bool Fail, SignedOut, NotifyOnAccountRead;
        public string? Email = "example@example.test";
        public int UsedPercent = 25;
        public DateTimeOffset? Reset;
        public Action? OnRead;
        public bool Disposed, DisposalStarted, Connected = true;
        public bool IsConnected => Connected;
        public TaskCompletionSource? Gate, AccountGate, DisposeGate;
        public void Notify(string method, JsonElement payload) => Notification?.Invoke(method, payload);
        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            if (method == "account/read")
            {
                Interlocked.Increment(ref AccountReads);
                if (AccountGate is not null) await AccountGate.Task.WaitAsync(cancellationToken);
                if (NotifyOnAccountRead)
                {
                    NotifyOnAccountRead = false;
                    Notify("account/updated", Json("""{"authMode":"chatgpt"}"""));
                }
                return SignedOut ? Json("{\"account\":null}") : Json(JsonSerializer.Serialize(new { account = new { type = "chatgpt", email = Email } }));
            }
            Reads++;
            OnRead?.Invoke();
            var usedPercent = UsedPercent;
            var reset = Reset?.ToUnixTimeSeconds();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("SECRET remote token");
            return Json(JsonSerializer.Serialize(new { rateLimits = new { limitId = "codex", primary = new { usedPercent, windowDurationMins = 300, resetsAt = reset } } },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        }
        public async ValueTask DisposeAsync()
        {
            DisposalStarted = true;
            if (DisposeGate is not null) await DisposeGate.Task;
            Disposed = true;
        }
    }
}
