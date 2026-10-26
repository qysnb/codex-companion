using System.Diagnostics;
using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaRecoveryTests
{
    const string RemoteError = "SECRET synthetic remote error";
    static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(3);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        await StillConnectedTimeoutRecovery();
        await AccountVerificationRecovery();
        await IdleCleanupFailureRecovery();
        await DetachedNotificationsAreIgnored();
        await LateFactoryResultIsDisposed();
        await FreshPushSurvivesFailedRead();
    }

    static async Task StillConnectedTimeoutRecovery()
    {
        foreach (var cancelled in new[] { false, true })
        {
            var clock = new FakeClock();
            var original = new FakeConnection();
            var replacement = new FakeConnection { UsedPercent = 70 };
            var factory = new ConnectionSequence(original, replacement);
            await using var service = new QuotaService(factory.Connect, clock.Now) { IsVisible = true };
            var first = await Bound(service.RefreshAsync());
            Check(first.Status == QuotaStatus.Live && first.FiveHour?.RemainingPercent == 75,
                "Recovery must start with an authenticated quota reading.");

            original.QuotaError = cancelled ? new OperationCanceledException(RemoteError) : new TimeoutException(RemoteError);
            clock.AdvanceSeconds(30);
            var failedAt = clock.Now();
            var failed = await Bound(service.RefreshAsync(true));
            Check(original.IsConnected && original.Disposed,
                "A request deadline must discard a transport even when IsConnected remains true.");
            Check(failed.Status == QuotaStatus.Stale && failed.FiveHour?.RemainingPercent == 75 && failed.UpdatedAt == first.UpdatedAt,
                "A verified-account deadline must preserve the last real quota and its original timestamp as stale.");
            Check(failed.FailureKind == QuotaFailureKind.Timeout && failed.RetryAt == failedAt.AddSeconds(5),
                "TimeoutException and non-shutdown cancellation must report a timeout with the first five-second retry deadline.");
            Check(failed.Error is not null && !failed.Error.Contains("SECRET", StringComparison.Ordinal),
                "A quota failure must never expose the synthetic remote error text.");

            clock.AdvanceSeconds(4);
            await Bound(service.RefreshAsync());
            Check(factory.Count == 1, "An ordinary refresh must respect the failure retry deadline.");
            clock.AdvanceSeconds(1);
            var recovered = await Bound(service.RefreshAsync());
            Check(factory.Count == 2 && replacement.QuotaReads == 1 && recovered.Status == QuotaStatus.Live && recovered.FiveHour?.RemainingPercent == 30,
                "The permitted retry must connect a replacement instead of reusing an unresponsive transport.");
            Check(recovered.FailureKind == QuotaFailureKind.None && recovered.RetryAt is null,
                "A successful replacement read must clear the failure category and retry deadline.");
        }
        Console.WriteLine("PASS still-connected timeout/cancellation recovery, sanitized errors, five-second replacement retry");
    }

    static async Task AccountVerificationRecovery()
    {
        foreach (var identity in new string?[] { "account-a@example.test", "account-b@example.test", null })
        {
            var clock = new FakeClock();
            var original = new FakeConnection();
            var unverified = new FakeConnection { AccountError = new TimeoutException(RemoteError) };
            var verified = new FakeConnection { Identity = identity, QuotaError = new TimeoutException(RemoteError) };
            var factory = new ConnectionSequence(original, unverified, verified);
            await using var service = new QuotaService(factory.Connect, clock.Now) { IsVisible = true };
            var first = await Bound(service.RefreshAsync());

            clock.AdvanceSeconds(10);
            await service.StartAsync();
            await WaitUntil(() => original.Disposed, "The first transport must be released before the identity-verification test reconnects.");
            clock.AdvanceSeconds(20);
            var hidden = await Bound(service.RefreshAsync(true));
            Check(hidden.Status == QuotaStatus.Unavailable && hidden.FiveHour is null && hidden.Weekly is null,
                "An account lookup deadline must hide cached quota until the account is verified.");
            Check(hidden.FailureKind == QuotaFailureKind.Timeout && unverified.Disposed,
                "An account lookup deadline must be classified and its unresponsive transport discarded.");
            Check(hidden.Error is not null && !hidden.Error.Contains("SECRET", StringComparison.Ordinal),
                "Account lookup failures must sanitize remote errors.");

            clock.AdvanceSeconds(5);
            var failedQuota = await Bound(service.RefreshAsync());
            Check(factory.Count == 3, "The next permitted retry must verify identity using a new connection.");
            if (identity == original.Identity)
            {
                Check(failedQuota.Status == QuotaStatus.Stale && failedQuota.FiveHour?.RemainingPercent == 75 && failedQuota.UpdatedAt == first.UpdatedAt,
                    "After the same account is verified, a quota deadline must restore its hidden cached reading with the original timestamp.");
            }
            else
            {
                Check(failedQuota.Status == QuotaStatus.Unavailable && failedQuota.FiveHour is null && failedQuota.Weekly is null,
                    "A changed or unknown account must never restore another account's hidden cached quota.");
            }
        }
        Console.WriteLine("PASS private hidden cache, verified same-account restoration, changed/unknown account isolation");
    }

    static async Task IdleCleanupFailureRecovery()
    {
        var clock = new FakeClock();
        var original = new FakeConnection { DisposeError = new IOException("Synthetic idle cleanup failure.") };
        var replacement = new FakeConnection { UsedPercent = 60 };
        var factory = new ConnectionSequence(original, replacement);
        await using var service = new QuotaService(factory.Connect, clock.Now) { IsVisible = true };
        await Bound(service.RefreshAsync());
        clock.AdvanceSeconds(10);
        await service.StartAsync();
        await WaitUntil(() => original.Disposed, "Idle cleanup must reach the connection that throws during disposal.");

        clock.AdvanceSeconds(20);
        await service.StartAsync();
        await WaitUntil(() => replacement.QuotaReads == 1 && service.Snapshot.Status == QuotaStatus.Live,
            "An idle DisposeAsync IOException must not stop later scheduled quota refreshes.");
        Check(factory.Count == 2 && service.Snapshot.FiveHour?.RemainingPercent == 40,
            "The scheduler must continue with a replacement after detached transport cleanup fails.");
        Console.WriteLine("PASS scheduler survives idle transport disposal failure");
    }

    static async Task DetachedNotificationsAreIgnored()
    {
        var clock = new FakeClock();
        var original = new FakeConnection();
        var replacement = new FakeConnection { Identity = "account-b@example.test", UsedPercent = 60 };
        var factory = new ConnectionSequence(original, replacement);
        await using var service = new QuotaService(factory.Connect, clock.Now) { IsVisible = true };
        await Bound(service.RefreshAsync());
        var captured = original.CaptureNotification();
        Check(captured is not null, "The test must capture the original notification delegate before it is detached.");

        clock.AdvanceSeconds(10);
        await service.StartAsync();
        await WaitUntil(() => original.Disposed, "The old connection must be detached before its captured callbacks run.");
        clock.AdvanceSeconds(20);
        var current = await Bound(service.RefreshAsync(true));
        Check(current.Status == QuotaStatus.Live && current.FiveHour?.RemainingPercent == 40,
            "The replacement account must have a fresh authenticated snapshot.");

        captured!("account/rateLimits/updated", Quota(99));
        Check(service.Snapshot == current, "A captured quota notification from a detached connection must not overwrite the replacement account.");
        captured("account/updated", Json("""{"authMode":null}"""));
        Check(service.Snapshot == current, "A captured sign-out notification from a detached connection must not clear the replacement account.");
        Console.WriteLine("PASS detached captured quota and sign-out notifications are ignored");
    }

    static async Task LateFactoryResultIsDisposed()
    {
        var clock = new FakeClock();
        var entered = NewGate();
        var factoryResult = new TaskCompletionSource<ICodexConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new FakeConnection();
        var service = new QuotaService(_ => { entered.TrySetResult(); return factoryResult.Task; }, clock.Now);
        try
        {
            var refresh = service.RefreshAsync();
            await Bound(entered.Task);
            var disposing = service.DisposeAsync().AsTask();
            await Bound(disposing);
            Check(!late.Disposed, "The late connection must not exist in the service before the factory supplies it.");
            factoryResult.TrySetResult(late);
            await WaitUntil(() => late.Disposed,
                "A connection returned after a cancelled factory wait must eventually be disposed by its owner.");
            await Bound(refresh);
        }
        finally
        {
            factoryResult.TrySetResult(late);
            await Bound(service.DisposeAsync().AsTask());
        }
        Console.WriteLine("PASS prompt shutdown and disposal of a late cancellation-ignoring factory result");
    }

    static async Task FreshPushSurvivesFailedRead()
    {
        foreach (var accountStage in new[] { false, true })
        {
            var clock = new FakeClock();
            var connection = new FakeConnection();
            var factory = new ConnectionSequence(connection);
            await using var service = new QuotaService(factory.Connect, clock.Now) { IsVisible = true };
            await Bound(service.RefreshAsync());
            var entered = NewGate();
            var release = NewGate();
            async Task<JsonElement> FailAfterPush(CancellationToken cancellationToken)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                throw new TimeoutException(RemoteError);
            }
            if (accountStage) connection.AccountHandler = FailAfterPush;
            else connection.QuotaHandler = FailAfterPush;

            clock.AdvanceSeconds(30);
            var refresh = service.RefreshAsync(true);
            try
            {
                await Bound(entered.Task);
                clock.AdvanceSeconds(1);
                connection.Notify("account/rateLimits/updated", Quota(10));
                var pushed = service.Snapshot;
                Check(pushed.Status == QuotaStatus.Live && pushed.FiveHour?.RemainingPercent == 90 && pushed.UpdatedAt == clock.Now(),
                    "A current authenticated connection must accept a fresh quota push during an in-flight read.");
                release.TrySetResult();
                var completed = await Bound(refresh);
                Check(completed == pushed && service.Snapshot == pushed,
                    "A failed older account/quota read must neither downgrade nor erase a newer authenticated live push.");
                Check(completed.FailureKind == QuotaFailureKind.None && completed.RetryAt is null,
                    "A newer successful push must retain a healthy failure/retry state when an older request fails.");
            }
            finally { release.TrySetResult(); }
        }
        Console.WriteLine("PASS fresh authenticated push survives failures of older quota and account reads");
    }

    static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task Bound(Task task) => await task.WaitAsync(TestDeadline);
    static async Task<T> Bound<T>(Task<T> task) => await task.WaitAsync(TestDeadline);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static async Task WaitUntil(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TestDeadline) await Task.Delay(10);
        Check(condition(), message);
    }

    static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    static JsonElement Quota(int usedPercent) => Json(JsonSerializer.Serialize(new
    {
        rateLimits = new
        {
            limitId = "codex",
            primary = new { usedPercent, windowDurationMins = 300 },
            secondary = new { usedPercent = 40, windowDurationMins = 10080 }
        }
    }));

    sealed class FakeClock
    {
        long _ticks = DateTimeOffset.Parse("2026-10-25T00:00:00Z").Ticks;
        public DateTimeOffset Now() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void AdvanceSeconds(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    sealed class ConnectionSequence(params FakeConnection[] connections)
    {
        int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<ICodexConnection> Connect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _count) - 1;
            if (index >= connections.Length) throw new InvalidOperationException("Unexpected extra test connection.");
            return Task.FromResult<ICodexConnection>(connections[index]);
        }
    }

    sealed class FakeConnection : ICodexConnection
    {
        int _accountReads, _quotaReads, _disposeCalls;
        public event Action<string, JsonElement>? Notification;
        public bool IsConnected => true;
        public int AccountReads => Volatile.Read(ref _accountReads);
        public int QuotaReads => Volatile.Read(ref _quotaReads);
        public bool Disposed => Volatile.Read(ref _disposeCalls) > 0;
        public string? Identity = "account-a@example.test";
        public int UsedPercent = 25;
        public Exception? AccountError, QuotaError, DisposeError;
        public Func<CancellationToken, Task<JsonElement>>? AccountHandler, QuotaHandler;
        public Action<string, JsonElement>? CaptureNotification() => Notification;
        public void Notify(string method, JsonElement payload) => Notification?.Invoke(method, payload);

        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "account/read")
            {
                Interlocked.Increment(ref _accountReads);
                if (AccountHandler is not null) return AccountHandler(cancellationToken);
                if (AccountError is not null) return Task.FromException<JsonElement>(AccountError);
                return Task.FromResult(Json(JsonSerializer.Serialize(new { account = new { type = "chatgpt", email = Identity } })));
            }
            Check(method == "account/rateLimits/read", "The quota service must only issue expected read-only requests.");
            Interlocked.Increment(ref _quotaReads);
            if (QuotaHandler is not null) return QuotaHandler(cancellationToken);
            if (QuotaError is not null) return Task.FromException<JsonElement>(QuotaError);
            return Task.FromResult(Quota(UsedPercent));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (DisposeError is not null) throw DisposeError;
            return ValueTask.CompletedTask;
        }
    }
}
