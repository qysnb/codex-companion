using System.Diagnostics;
using System.Text.Json;
using CodexCompanion.Core;

public static class QuotaDeadlineTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        // The real server completed a valid rate-limit request after 16.3s.
        // Keep this regression just beyond the previous hard 10s deadline.
        foreach (var method in new[] { "account/read", "account/rateLimits/read" })
        {
            var connection = new SlowRead(method, TimeSpan.FromMilliseconds(10500));
            await using var service = new QuotaService(_ => Task.FromResult<ICodexConnection>(connection));
            var reading = service.RefreshAsync(true);
            await connection.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            _ = service.Snapshot;
            Check(watch.ElapsedMilliseconds < 150, "A slow server read must not block the in-memory UI snapshot.");
            var result = await reading.WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.Status == QuotaStatus.Live && result.FiveHour?.RemainingPercent == 75,
                "A valid " + method + " response after the old 10s boundary must complete instead of being cancelled and retried.");
            Check(connection.AccountReads == 1 && connection.QuotaReads == 1,
                "Slow but successful reads must not trigger duplicate requests.");
        }
        var pending = new SlowRead("account/rateLimits/read", Timeout.InfiniteTimeSpan);
        var stopping = new QuotaService(_ => Task.FromResult<ICodexConnection>(pending));
        var request = stopping.RefreshAsync(true);
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stopping.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        Check(pending.Disposed, "Shutdown must cancel a slow read promptly and release its owned connection.");
        Console.WriteLine("PASS slow successful account/quota responses beyond 10s, immediate memory snapshot, no duplicate read, prompt shutdown");
    }

    sealed class SlowRead(string delayedMethod, TimeSpan delay) : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification { add { } remove { } }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AccountReads, QuotaReads;
        public bool Disposed;
        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            if (method == "account/read") AccountReads++; else QuotaReads++;
            if (method == delayedMethod) { Entered.TrySetResult(); await Task.Delay(delay, cancellationToken); }
            return method == "account/read"
                ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", id = "synthetic-account" } })
                : JsonSerializer.SerializeToElement(new { rateLimits = new { limitId = "codex", primary = new { usedPercent = 25, windowDurationMins = 300 }, secondary = new { usedPercent = 40, windowDurationMins = 10080 } } });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
