using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaResetTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    static QuotaSnapshot Live(double? five, double? week) => new(QuotaStatus.Live,
        five is { } f ? new(f, 300, Now.AddHours(5)) : null,
        week is { } w ? new(w, 10080, Now.AddDays(7)) : null, Now);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var tracker = new QuotaResetTracker();
        Check(tracker.Observe("A", Live(100, 100)) is null, "Initial 100% must not notify.");
        tracker.Observe("A", Live(0, 20));
        Check(tracker.Observe("A", Live(100, 20)) is { FiveHour: true, Weekly: false }, "A real five-hour 0% to 100% transition must notify once.");
        Check(tracker.Observe("A", Live(100, 20)) is null, "Repeated 100% must not notify twice.");
        tracker.Observe("A", Live(0, 0));
        Check(tracker.Observe("A", Live(100, 100)) is { FiveHour: true, Weekly: true }, "Simultaneous resets must be combined.");
        tracker.Observe("A", Live(0, 0));
        tracker.Observe("A", new(QuotaStatus.Stale, null, null, Now));
        tracker.Observe("A", Live(null, 50));
        Check(tracker.Observe("A", Live(100, 100)) is { FiveHour: true, Weekly: false }, "Missing windows and failures preserve the last observed window; a positive intermediate value disarms it.");
        tracker.Observe("A", Live(0, 0));
        Check(tracker.Observe("B", Live(100, 100)) is null, "An account change must never trigger a reset reminder.");
        tracker.Observe("B", Live(0, 0)); tracker.Observe(null, Live(100, 100));
        Check(tracker.Observe("B", Live(100, 100)) is null, "An unknown identity must clear the reset baseline.");
        tracker.Observe("B", Live(0, 0)); tracker.Clear();
        Check(tracker.Observe("B", Live(100, 100)) is null, "Logout/clear must discard the baseline.");
        tracker.Observe("B", Live(.1, 99.9));
        Check(tracker.Observe("B", Live(100, 100)) is null, "Only actual zero, not rounded display zero, arms the reminder.");
        var json = "{}";
        Check(JsonSerializer.Deserialize<CompanionSettings>(json)!.QuotaResetNotifications, "Existing settings must default reset notifications to on.");
        var settings = new CompanionSettings { QuotaResetNotifications = false };
        Check(!JsonSerializer.Deserialize<CompanionSettings>(JsonSerializer.Serialize(settings))!.Normalize().QuotaResetNotifications, "Disabled notifications must survive settings serialization.");
        Integration().GetAwaiter().GetResult();
        LocalIntegration().GetAwaiter().GetResult();
        SourceIsolation().GetAwaiter().GetResult();
        Console.WriteLine("PASS exact reset transitions, combined windows, sparse updates, deduplication, account isolation, default-on and persisted opt-out");
    }

    static async Task SourceIsolation()
    {
        var now = Now; var connection = new Connection { Used = 0 };
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection), () => now)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        var notices = new List<QuotaResetNotice>(); service.ResetDetected += notices.Add;
        void Send(double remaining)
        {
            now = now.AddSeconds(1);
            service.AcceptLocalReading(new(Live(remaining, remaining) with { UpdatedAt = now }, 1, now));
        }
        Send(0); now = now.AddSeconds(1); await service.RefreshAsync(true);
        Check(service.Source == "app-server" && notices.Count == 0, "A first online 100% must not pair with a local zero.");
        Send(100);
        Check(notices.Count == 0, "Returning to local sync must establish a new baseline after another source was selected.");
        Send(0); Send(100);
        Check(notices.Count == 1 && service.IsCurrentResetNotice(notices[0]), "Local resets must work after re-establishing the baseline.");
        now = now.AddSeconds(1); connection.Push(100);
        Check(service.Source == "app-server" && !service.IsCurrentResetNotice(notices[0]), "Switching to newer online data must invalidate a queued local notice.");
        Send(100);
        connection.Push(0);
        Check(notices.Count == 1, "An older nonselected online source must not trigger a reminder.");
        service.LocalSyncEnabled = false; now = now.AddSeconds(1); connection.Push(0);
        Check(notices.Count == 1, "Online reminders must not reuse a discarded cross-source baseline.");
        Console.WriteLine("PASS local/online baseline isolation, queued notice invalidation and nonselected-source suppression");
    }

    static async Task LocalIntegration()
    {
        var now = Now; var connects = 0;
        await using var service = new QuotaService(_ => { connects++; throw new InvalidOperationException("Local reminders must not connect."); }, () => now)
            { NetworkMode = QuotaNetworkMode.Local, LocalSyncEnabled = true };
        var notices = new List<QuotaResetNotice>();
        service.ResetDetected += notices.Add;
        QuotaSnapshot Send(double? five, double? week, long generation = 1)
        {
            now = now.AddSeconds(1);
            var snapshot = Live(five, week) with { UpdatedAt = now };
            Check(service.AcceptLocalReading(new(snapshot, generation, now)), "The local fixture must be accepted.");
            return snapshot;
        }
        Send(100, 100);
        Check(notices.Count == 0, "Initial local 100% establishes a baseline without notifying.");
        Send(0, 50); var recovered = Send(100, 50);
        Check(notices.Count == 1 && notices[0] is { FiveHour: true, Weekly: false } && service.IsCurrentResetNotice(notices[0]),
            "A fresh local five-hour 0% to 100% transition must dispatch a valid notice.");
        service.AcceptLocalReading(new(recovered, 1, now.AddMilliseconds(100), true));
        Send(100, 50);
        Check(notices.Count == 1, "Local rereads and duplicate values must not repeat reminders.");
        Send(0, 0); Send(100, 100);
        Check(notices.Count == 2 && notices[1] is { FiveHour: true, Weekly: true }, "Both local windows must share one reminder.");
        Send(0, 0); Send(null, 10); Send(100, 100);
        Check(notices.Count == 3 && notices[2] is { FiveHour: true, Weekly: false }, "Missing windows preserve observed zero, positive intermediate values disarm it.");
        Send(.1, 99.9); Send(100, 100);
        Check(notices.Count == 3, "Rounded display zero must never arm local reminders.");
        Send(0, 0); Send(100, 100, 2);
        Check(notices.Count == 3 && !service.IsCurrentResetNotice(notices[2]), "A new local generation must discard the old baseline and queued notice.");
        Send(0, 0, 2); service.InvalidateLocalReading(3, true); Send(100, 100, 3);
        Check(notices.Count == 3, "An account invalidation must not join old zero with new account's 100%.");
        Send(0, 0, 3); service.LocalSyncEnabled = false; service.LocalSyncEnabled = true; Send(100, 100, 3);
        Check(notices.Count == 3, "Re-enabling local sync establishes a new baseline.");
        var zero = Send(0, 0, 3);
        service.AcceptLocalReading(new(zero with { FiveHour = Live(100, 0).FiveHour }, 3, now));
        Check(notices.Count == 3, "Equal-timestamp records must not manufacture a reset transition.");
        service.InvalidateLocalReading(4, false); Send(100, 100, 4);
        Check(notices.Count == 3, "A source invalidation must discard the baseline even without an account change.");
        await service.RefreshAsync();
        Check(connects == 0, "Local recovery reminders must perform no network reads.");
        Console.WriteLine("PASS local reset reminders, fresh events only, deduplication, combined windows, sparse values, generation/account invalidation and zero network reads");
    }

    static async Task Integration()
    {
        var first = new Connection(); var next = new Connection { Used = 0 };
        var count = 0;
        await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(++count == 1 ? first : next));
        var notices = new List<QuotaResetNotice>();
        service.ResetDetected += notices.Add;
        await service.RefreshAsync();
        Check(notices.Count == 0, "Initial exhausted quota does not notify.");
        first.Push(0);
        Check(notices.Count == 1 && notices[0] is { FiveHour: true, Weekly: true }, "A real authenticated push must emit one combined service reminder.");
        first.Push(0);
        Check(notices.Count == 1, "Repeated pushes must not duplicate service reminders.");
        first.Push(100);
        first.Timeout = true;
        await service.RefreshAsync(true);
        Check(service.Snapshot.Status == QuotaStatus.Unavailable, "The test must exercise a failed account verification before reconnection.");
        await service.RefreshAsync(true);
        Check(notices.Count == 2 && service.IsCurrentResetNotice(notices[1]), "Reconnection and same-account verification must preserve an observed zero across a read failure.");
        next.Push(100);
        next.Identity = "B";
        next.Used = 0;
        await service.RefreshAsync(true);
        Check(notices.Count == 2 && !service.IsCurrentResetNotice(notices[1]), "An account change must neither notify nor leave an old dispatched notice valid.");
        Console.WriteLine("PASS service reset event from authenticated push and reconnect, duplicate suppression and stale-notice invalidation");
    }

    sealed class Connection : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification;
        public int Used = 100;
        public string Identity = "A";
        public bool Timeout;
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        static JsonElement Quota(int used) => Json(new { rateLimits = new { limitId = "codex", primary = new { usedPercent = used, windowDurationMins = 300 }, secondary = new { usedPercent = used, windowDurationMins = 10080 } } });
        public void Push(int used) => Notification?.Invoke("account/rateLimits/updated", Quota(used));
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Timeout ? Task.FromException<JsonElement>(new TimeoutException("synthetic deadline"))
                : Task.FromResult(method == "account/read" ? Json(new { account = new { type = "chatgpt", id = Identity } }) : Quota(Used));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
