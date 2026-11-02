using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexCompanion.Core;

// Credentials stay in memory. Never serialize this object or include it in diagnostics.
public sealed class QuotaCredential(string accessToken, string? accountId, string userId)
{
    internal string AccessToken { get; } = accessToken;
    internal string? AccountId { get; } = accountId;
    public string Identity { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((accountId ?? "") + "\n" + userId)));
    public override string ToString() => "[redacted quota credential]";

    internal static QuotaCredential? Load()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            using var stream = new FileStream(Path.Combine(home, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 65536) return null;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens)) return null;
            var token = Text(tokens, "access_token");
            if (string.IsNullOrWhiteSpace(token)) return null;
            var account = Text(tokens, "account_id");
            var pieces = token.Split('.');
            if (pieces.Length != 3) return null;
            var payload = pieces[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            var user = Text(claims.RootElement, "sub");
            if (claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
            { account ??= Text(auth, "chatgpt_account_id"); user ??= Text(auth, "chatgpt_user_id"); }
            return string.IsNullOrWhiteSpace(user) ? null : new(token, account, user);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException) { return null; }
    }
    static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

public sealed class QuotaHttpException(string reason, bool allowFallback, bool invalidateIdentity = false, TimeSpan? retryAfter = null, bool rateLimited = false) : IOException(reason)
{
    public bool AllowFallback { get; } = allowFallback;
    public bool InvalidateIdentity { get; } = invalidateIdentity;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool RateLimited { get; } = rateLimited;
}
public sealed record QuotaHttpStats(long Requests, long Successes, long Failures, long ResponseBodyBytes, int? LastStatus, double? LastDurationMs);
public sealed record HttpQuotaReading(string Identity, QuotaSnapshot Snapshot);

public sealed class CodexHttpQuotaClient : IDisposable
{
    readonly Func<QuotaCredential?> _credentials;
    readonly HttpClient _client;
    readonly object _sync = new();
    long _requests, _successes, _failures, _bodyBytes;
    int? _status;
    double? _duration;
    public CodexHttpQuotaClient(Func<QuotaCredential?>? credentials = null, HttpMessageHandler? handler = null)
    {
        _credentials = credentials ?? QuotaCredential.Load;
        _client = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxResponseHeadersLength = 32, MaxConnectionsPerServer = 1
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public QuotaHttpStats Stats { get { lock (_sync) return new(_requests, _successes, _failures, _bodyBytes, _status, _duration); } }
    public string? CurrentIdentity => _credentials()?.Identity;
    public async Task<HttpQuotaReading> ReadAsync(CancellationToken cancellationToken)
    {
        var credential = _credentials() ?? throw new QuotaHttpException("Existing ChatGPT credentials are unavailable.", true, true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var started = Stopwatch.GetTimestamp();
        lock (_sync) { _requests++; _status = null; }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new("Bearer", credential.AccessToken);
            if (!string.IsNullOrWhiteSpace(credential.AccountId)) request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("CodexCompanion/0.6.5");
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            lock (_sync) _status = status;
            if (!response.IsSuccessStatusCode)
            {
                var after = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null);
                throw new QuotaHttpException("Quota HTTP status " + status + ".", status is 401 or 403 or 408 || status >= 500, status is 401 or 403, after, status == 429);
            }
            if (response.Content.Headers.ContentLength > 65536) throw new QuotaHttpException("Quota response exceeds the size limit.", false);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                lock (_sync) _bodyBytes += count;
                if (bounded.Length + count > 65536) throw new QuotaHttpException("Quota response exceeds the size limit.", false);
                bounded.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(bounded.ToArray());
            var snapshot = Parse(json.RootElement, DateTimeOffset.UtcNow);
            if (_credentials()?.Identity != credential.Identity) throw new QuotaHttpException("Account changed during quota read.", false, true);
            lock (_sync) _successes++;
            return new(credential.Identity, snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { lock (_sync) _failures++; throw new QuotaHttpException("Quota HTTP timed out.", true); }
        catch (HttpRequestException) { lock (_sync) _failures++; throw new QuotaHttpException("Quota HTTP connection failed.", true); }
        catch (IOException e) when (e is not QuotaHttpException) { lock (_sync) _failures++; throw new QuotaHttpException("Quota HTTP stream failed.", true); }
        catch (JsonException) { lock (_sync) _failures++; throw new QuotaHttpException("Quota HTTP response is invalid.", true); }
        catch { lock (_sync) _failures++; throw; }
        finally { lock (_sync) _duration = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1); }
    }
    public static QuotaSnapshot Parse(JsonElement root, DateTimeOffset now)
    {
        QuotaWindow? five = null, week = null;
        if (!root.TryGetProperty("rate_limit", out var limits) || limits.ValueKind != JsonValueKind.Object)
            throw new QuotaHttpException("Quota HTTP windows are unavailable.", true);
        foreach (var property in limits.EnumerateObject())
        {
            var w = property.Value;
            if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("limit_window_seconds", out var seconds) || !seconds.TryGetInt32(out var duration) ||
                !w.TryGetProperty("used_percent", out var used) || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
            DateTimeOffset? reset = null;
            if (w.TryGetProperty("reset_at", out var r) && r.TryGetInt64(out var unix))
                try { reset = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
            var window = new QuotaWindow(Math.Clamp(100 - percent, 0, 100), duration / 60, reset);
            if (duration == 18000) five = window;
            if (duration == 604800) week = window;
        }
        if (five is null && week is null) throw new QuotaHttpException("Quota HTTP windows are unavailable.", false);
        return new(QuotaStatus.Live, five, week, now);
    }
    public void Dispose() => _client.Dispose();
}
