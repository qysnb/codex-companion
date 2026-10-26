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
        Console.WriteLine("PASS exact reset transitions, combined windows, sparse updates, deduplication, account isolation, default-on and persisted opt-out");
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
