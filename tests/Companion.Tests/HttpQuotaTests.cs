using System.Net;
using System.Text;
using CodexCompanion.Core;

public static class HttpQuotaTests
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var credential = new QuotaCredential("synthetic-token", "synthetic-account", "synthetic-user");
        var handler = new Handler();
        using var client = new CodexHttpQuotaClient(() => credential, handler);
        handler.Response = () => new(HttpStatusCode.OK) { Content = new StringContent("{\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000,\"reset_at\":2000000000},\"secondary_window\":{\"used_percent\":0,\"limit_window_seconds\":604800,\"reset_at\":2000500000}}}", Encoding.UTF8, "application/json") };
        var result = await client.ReadAsync(default);
        Require(result.Snapshot.FiveHour?.RemainingPercent == 88 && result.Snapshot.Weekly?.RemainingPercent == 100, "HTTP windows must be identified by duration.");
        Require(handler.Requests == 1 && client.Stats.Successes == 1, "Exactly one quota request is required.");
        await using (var service = new QuotaService(_ => throw new Exception("Unexpected RPC fallback"), () => now, client) { LocalSyncEnabled = true })
        {
            service.AcceptLocalReading(new(new(QuotaStatus.Live, new(1, 300, now.AddHours(5)), null, now.AddSeconds(1)), 1, now));
            await service.RefreshAsync();
            Require(service.Source == "http" && service.Snapshot.FiveHour?.RemainingPercent == 88, "Fresh local events must neither suppress nor override HTTP quota.");
        }
        using var limited = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => { var r = new HttpResponseMessage((HttpStatusCode)429); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(120)); return r; } });
        try { await limited.ReadAsync(default); throw new Exception("Accepted 429"); }
        catch (QuotaHttpException e) { Require(!e.AllowFallback && e.RetryAfter >= TimeSpan.FromSeconds(120), "429 must respect Retry-After without RPC fallback."); }
        using var changed = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => { credential = new("new-token", "another-account", "another-user"); return handler.Response(); } });
        try { await changed.ReadAsync(default); throw new Exception("Accepted account switch"); }
        catch (QuotaHttpException e) { Require(!e.AllowFallback && e.InvalidateIdentity, "An in-flight account change must discard the response."); }
        var fallbacks = 0;
        using var rejected = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => new(HttpStatusCode.Unauthorized) });
        await using (var recovery = new QuotaService(_ => { fallbacks++; throw new IOException("synthetic recovery unavailable"); }, () => now, rejected))
        {
            await recovery.RefreshAsync(true); now = now.AddSeconds(30); await recovery.RefreshAsync(true);
            Require(fallbacks == 1 && rejected.Stats.Requests == 2, "HTTP is primary each time, but recovery processes must be limited to one per 15 minutes.");
        }
        using var throttled = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => { var r = new HttpResponseMessage((HttpStatusCode)429); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(120)); return r; } });
        await using (var recovery = new QuotaService(_ => throw new Exception("429 must not start RPC"), () => now, throttled))
        {
            await recovery.RefreshAsync(true); await recovery.RefreshAsync(true);
            Require(throttled.Stats.Requests == 1 && recovery.Snapshot.RetryAt >= now.AddSeconds(120), "Manual requests must also honor server Retry-After.");
        }
        using var oversized = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) } });
        try { await oversized.ReadAsync(default); throw new Exception("Accepted oversized response"); }
        catch (QuotaHttpException e) { Require(!e.AllowFallback, "Oversized responses must stay bounded without heavy recovery."); }
        var missing = CodexHttpQuotaClient.Parse(System.Text.Json.JsonDocument.Parse("{\"rate_limit\":{\"secondary_window\":{\"used_percent\":100,\"limit_window_seconds\":18000}}}").RootElement, now);
        Require(missing.FiveHour?.RemainingPercent == 0 && missing.Weekly is null, "Window position is irrelevant and absent windows must remain unknown.");
        using var recovering = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => new(HttpStatusCode.ServiceUnavailable) });
        var rpc = new RecoveryConnection();
        await using (var recovery = new QuotaService(_ => Task.FromResult<ICodexConnection>(rpc), () => now, recovering))
        {
            await recovery.RefreshAsync(true);
            Require(recovery.Source == "app-server" && recovery.Snapshot.FiveHour?.RemainingPercent == 65 && rpc.Reads == 2, "Eligible failures must recover through read-only account and quota RPC.");
            now = now.AddSeconds(31); await recovery.RefreshAsync(true);
            Require(recovery.Snapshot.Status == QuotaStatus.Stale && recovery.Snapshot.FiveHour?.RemainingPercent == 65 && rpc.Reads == 2,
                "Unchanged credentials must preserve the verified fallback cache while recovery is rate-limited.");
        }
        using var noHeader = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => new((HttpStatusCode)429) });
        await using (var recovery = new QuotaService(_ => throw new Exception("Unexpected429 recovery"), () => now, noHeader))
        {
            await recovery.RefreshAsync(true); await recovery.RefreshAsync(true);
            Require(noHeader.Stats.Requests == 1, "A429 without Retry-After also needs a manual-request cooldown.");
        }
        using var cooldown = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => { var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(120)); return r; } });
        await using (var recovery = new QuotaService(_ => Task.FromResult<ICodexConnection>(new RecoveryConnection()), () => now, cooldown))
        {
            await recovery.RefreshAsync(true);
            var deadline = typeof(QuotaService).GetMethod("NetworkDeadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Require((DateTimeOffset)deadline.Invoke(recovery, [now.AddSeconds(31)])! >= now.AddSeconds(120), "Scheduler must sleep through server cooldown after successful recovery.");
            recovery.InvalidateLocalReading(2, true);
            Require((DateTimeOffset)deadline.Invoke(recovery, [now.AddSeconds(31)])! >= now.AddSeconds(120), "Account invalidation must not cause a busy loop during cooldown.");
        }
        using var scheduledClient = new CodexHttpQuotaClient(() => credential, new Handler { Response = handler.Response });
        await using (var scheduled = new QuotaService(_ => throw new Exception("Unexpected scheduled recovery"), () => now, scheduledClient) { LocalSyncEnabled = true })
        {
            await scheduled.StartAsync();
            await Until(() => scheduledClient.Stats.Successes == 1 && !scheduled.IsRefreshing);
            now = now.AddSeconds(31);
            scheduled.AcceptLocalReading(new(new(QuotaStatus.Live, new(1, 300, now.AddHours(5)), null, now), 1, now));
            await Until(() => scheduledClient.Stats.Successes == 2 && !scheduled.IsRefreshing);
            Require(scheduled.Source == "http" && scheduled.Snapshot.FiveHour?.RemainingPercent == 88, "Thirty-second scheduler must query despite fresh local events.");
        }
        using var cancelling = new CodexHttpQuotaClient(() => credential, new BlockingHandler());
        using var stop = new CancellationTokenSource(30);
        try { await cancelling.ReadAsync(stop.Token); throw new Exception("Ignored cancellation"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        using var identityRecovery = new CodexHttpQuotaClient(() => credential, new Handler { Response = () => new(HttpStatusCode.ServiceUnavailable) });
        await using (var recovery = new QuotaService(_ => Task.FromResult<ICodexConnection>(new RecoveryConnection(() => credential = new("changed-during-rpc", "switched-account", "switched-user"))), () => now, identityRecovery))
        {
            await recovery.RefreshAsync(true);
            Require(recovery.Snapshot.FiveHour is null && recovery.Snapshot.Weekly is null && recovery.Snapshot.Status == QuotaStatus.Unavailable,
                "An account change during RPC recovery must discard the fallback response.");
        }
        Console.WriteLine("PASS HTTP quota single request, duration mapping, local authority, 429 and account switch");
    }
    static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(5); } throw new Exception("HTTP scheduling condition timed out"); }
    sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return new(HttpStatusCode.OK); }
    }
    sealed class RecoveryConnection(Action? quotaAction = null) : ICodexConnection
    {
        public int Reads;
        public event Action<string, System.Text.Json.JsonElement>? Notification { add { } remove { } }
        public Task<System.Text.Json.JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Reads++;
            if (method == "account/rateLimits/read") quotaAction?.Invoke();
            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(method == "account/read" ? (object)new { account = new { type = "chatgpt", id = "synthetic-recovery" } } : new { rateLimits = new { primary = new { usedPercent = 35, windowDurationMins = 300 } } }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpResponseMessage> Response = () => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Require(request.RequestUri?.AbsoluteUri == "https://chatgpt.com/backend-api/wham/usage" && request.Method == HttpMethod.Get, "Only the fixed quota endpoint is permitted.");
            Require(request.Headers.Authorization?.Scheme == "Bearer", "Authentication is required.");
            return Task.FromResult(Response());
        }
    }
}
