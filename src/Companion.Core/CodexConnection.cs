using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace CodexCompanion.Core;
public interface ICodexConnection : IAsyncDisposable
{
    bool IsConnected => true;
    event Action<string, JsonElement>? Notification;
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken);
}

/// <summary>A private stdio client; it never takes ownership of the desktop app's server.</summary>
public sealed class CodexConnection : ICodexConnection
{
    public static string? LastDiagnostic { get; private set; }
    public static bool CaptureDiagnostics { get; set; }
    static readonly ConcurrentQueue<string> diagnosticEvents = new();
    static long diagnosticStarted;
    public static string[] DiagnosticEvents => diagnosticEvents.ToArray();
    static void Trace(string value)
    { if (!CaptureDiagnostics) return; diagnosticEvents.Enqueue(Stopwatch.GetElapsedTime(diagnosticStarted).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms " + value); while (diagnosticEvents.Count > 32) diagnosticEvents.TryDequeue(out _); }
    readonly Process _process;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly Task _reader, _stderr;
    long _nextId;
    int _closed, _disposed;
    public event Action<string, JsonElement>? Notification;
    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    CodexConnection(Process process)
    {
        _process = process;
        _reader = ReadAsync();
        _stderr = DrainErrorsAsync();
    }

    public static async Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        LastDiagnostic = null;
        diagnosticStarted = Stopwatch.GetTimestamp();
        diagnosticEvents.Clear();
        var executable = FindExecutable();
        var attempts = new List<string>();
        // The proxy observes the running desktop server. An older CLI or an absent
        // control socket falls back to an independent, read-only app-server client.
        foreach (var proxy in new[] { true, false })
        {
            CodexConnection? connection = null;
            try
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                start.ArgumentList.Add("app-server");
                if (proxy) start.ArgumentList.Add("proxy");
                else { start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://"); }
                var process = Process.Start(start) ?? throw new IOException("Codex could not be started.");
                Trace("start:" + (proxy ? "proxy" : "stdio"));
                connection = new CodexConnection(process);
                await connection.RequestAsync("initialize", new
                {
                    clientInfo = new { name = "codex_companion", title = "Codex Companion", version = "0.4.6" },
                    capabilities = new { experimentalApi = false }
                }, cancellationToken).ConfigureAwait(false);
                await connection.WriteAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                attempts.Add((proxy ? "proxy" : "stdio") + ":" + error.GetType().Name);
                Trace(attempts[^1]);
                LastDiagnostic = string.Join(", ", attempts);
                if (!proxy) throw new IOException("Codex connection failed (" + string.Join(", ", attempts) + ").");
            }
        }
        throw new IOException("Codex is unavailable. Open Codex and try again.");
    }

    static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), OperatingSystem.IsWindows() ? "codex.exe" : "codex");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        }
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(install))
        {
            try
            {
                var candidates = Directory.EnumerateDirectories(install)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(folder => Path.Combine(folder, "codex.exe"))
                    .Prepend(Path.Combine(install, "codex.exe"));
                foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        throw new FileNotFoundException("Codex CLI was not found. Open or install Codex and try again.");
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0) throw new IOException("The Codex connection is closed.");
        var id = Interlocked.Increment(ref _nextId);
        Trace("send:" + method + ":" + id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(CodexRequestPolicy.TimeoutFor(method));
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex did not respond in time.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closed) != 0) throw new IOException("The Codex connection is closed.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var receivedMethod) && receivedMethod.ValueKind == JsonValueKind.String)
                {
                    var knownMethod = receivedMethod.GetString();
                    Trace("notification:" + (knownMethod is "account/updated" or "account/rateLimits/updated" or "account/chatgptAuthTokens/refresh" ? knownMethod : "other"));
                }
                if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
                {
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        // Account reads must never cause this client to provide credentials
                        // or approve an unrelated server request.
                        await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Method unavailable." } }, _stop.Token).ConfigureAwait(false);
                    }
                    else if (message.TryGetProperty("params", out var payload))
                    {
                        var notification = payload.Clone();
                        foreach (var handler in Notification?.GetInvocationList() ?? [])
                            try { ((Action<string, JsonElement>)handler)(method.GetString()!, notification); } catch { }
                    }
                    continue;
                }
                if (!message.TryGetProperty("id", out var responseId)) continue;
                long id;
                if (responseId.ValueKind == JsonValueKind.Number) { if (!responseId.TryGetInt64(out id)) continue; }
                else if (responseId.ValueKind != JsonValueKind.String || !long.TryParse(responseId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                if (!_pending.TryRemove(id, out var completion)) continue;
                Trace("response:" + id);
                if (message.TryGetProperty("error", out var rpcError))
                {
                    var code = rpcError.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number.ToString(CultureInfo.InvariantCulture) : "unknown";
                    LastDiagnostic = "RPC code " + code;
                    completion.TrySetException(new IOException("Codex RPC failed (code " + code + ")."));
                }
                else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                else completion.TrySetException(new IOException("Codex returned an invalid response."));
            }
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { Trace("reader:" + error.GetType().Name); }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new IOException("The Codex connection was interrupted."));
        }
    }

    async Task DrainErrorsAsync()
    {
        // Server logs can contain account information. Drain the pipe without
        // storing, displaying, or forwarding any of its contents.
        try { while (await _process.StandardError.ReadLineAsync(_stop.Token).ConfigureAwait(false) is not null) { } }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _closed, 1);
        _stop.Cancel();
        try { _process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        try { if (!_process.HasExited) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        // This PID is always the process started above, never a discovered app PID.
        try { if (!_process.HasExited) _process.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await Task.WhenAll(_reader, _stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        _process.Dispose();
        _stop.Dispose();
    }
}
