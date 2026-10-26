using System.Text.Json;
namespace CodexCompanion.Core;

public sealed class QuotaService : IAsyncDisposable
{
    readonly object _sync = new();
    readonly Func<CancellationToken, Task<ICodexConnection>> _connect;
    readonly Func<DateTimeOffset> _clock;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly HashSet<long> _observedResets = new();
    readonly HashSet<long> _scheduledResets = new();
    readonly QuotaResetTracker _resetTracker = new();
    QuotaResetNotice? _lastResetNotice;
    ICodexConnection? _connection;
    Action<string, JsonElement>? _notificationHandler;
    Task<QuotaSnapshot>? _refresh;
    Task? _loop;
    QuotaSnapshot _snapshot = new(QuotaStatus.Loading, null, null, null);
    DateTimeOffset? _lastSuccess;
    string? _accountIdentity;
    QuotaSnapshot? _lastKnownQuota;
    string? _lastKnownIdentity;
    DateTimeOffset _nextPeriodic = DateTimeOffset.MinValue, _retryAt = DateTimeOffset.MinValue, _refreshStartedAt;
    TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);
    long _accountEpoch, _quotaRevision;
    int _failures;
    bool _visible, _signedOut, _disposed, _readingInitialAccount, _prefetchPending, _accountKnownOnConnection, _resetPending, _notificationPending;
    DateTimeOffset _connectionUsed;
    public event Action<QuotaSnapshot>? Updated;
    public event Action<QuotaResetNotice>? ResetDetected;
    public bool IsCurrentResetNotice(QuotaResetNotice notice)
    { lock (_sync) return !_disposed && ReferenceEquals(_lastResetNotice, notice); }

    public QuotaService(Func<CancellationToken, Task<ICodexConnection>>? connect = null, Func<DateTimeOffset>? clock = null)
    {
        _connect = connect ?? CodexConnection.ConnectAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public QuotaSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public bool IsRefreshing { get { lock (_sync) return _refresh is { IsCompleted: false }; } }
    public TimeSpan RefreshInterval
    {
        get { lock (_sync) return _refreshInterval; }
        set
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 5, 3600));
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_refreshInterval == interval) return;
                _refreshInterval = interval;
                if (_lastSuccess is { } last) _nextPeriodic = last + interval;
            }
            Wake();
        }
    }
    public bool IsVisible
    {
        get { lock (_sync) return _visible; }
        set
        {
            lock (_sync) { if (_visible == value || _disposed) return; _visible = value; }
            Wake();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Connecting can perform synchronous process setup before its first await.
            // Keep startup and the UI thread independent of that work.
            if (_loop is null)
            {
                _prefetchPending = _lastSuccess is null && _refresh is not { IsCompleted: false };
                _loop = Task.Run(PollAsync);
            }
        }
        Wake();
        return Task.CompletedTask;
    }

    public Task<QuotaSnapshot> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        => RequestRefresh(force, scheduled: false, cancellationToken);

    Task<QuotaSnapshot> RequestRefresh(bool force, bool scheduled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<QuotaSnapshot> completion;
        long epoch, revision;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock();
            ObserveElapsedResets(now);
            if (!force && !_resetPending && !_notificationPending && _lastSuccess is { } last && now - last < TimeSpan.FromSeconds(30) &&
                _snapshot.Status is QuotaStatus.Live or QuotaStatus.NotLoggedIn)
            {
                return Task.FromResult(_snapshot);
            }
            if (_refresh is { IsCompleted: false })
                return !force && _failures > 0 && (_snapshot.FiveHour is not null || _snapshot.Weekly is not null)
                    ? Task.FromResult(_snapshot) : _refresh.WaitAsync(cancellationToken);
            if (scheduled && now < NetworkDeadline(now)) return Task.FromResult(_snapshot);
            if ((!force || scheduled) && _failures > 0 && now < _retryAt) return Task.FromResult(_snapshot);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refresh = completion.Task;
            _connectionUsed = now;
            _refreshStartedAt = now;
            _prefetchPending = _resetPending = _notificationPending = false;
            epoch = _accountEpoch;
            revision = _quotaRevision;
        }
        // A caller cancelling its wait must not cancel another caller's shared read.
        _ = Task.Run(() => RefreshCoreAsync(completion, epoch, revision));
        Wake();
        return completion.Task.WaitAsync(cancellationToken);
    }

    async Task RefreshCoreAsync(TaskCompletionSource<QuotaSnapshot> completion, long epoch, long startedRevision)
    {
        ICodexConnection? connection = null;
        var stage = "connect";
        var identityVerified = false;
        var unattached = false;
        try
        {
            lock (_sync) { if (_disposed || epoch != _accountEpoch) return; connection = _connection; }
            if (connection is null)
            {
                QuotaSnapshot? pending = null;
                lock (_sync)
                    if (_snapshot.FiveHour is not null || _snapshot.Weekly is not null)
                        pending = _snapshot = _snapshot with { Status = QuotaStatus.Stale };
                if (pending is not null) RaiseUpdated(pending);
                var connecting = _connect(_stop.Token);
                try { connection = await connecting.WaitAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                { _ = DisposeLateConnectionAsync(connecting); throw; }
                unattached = true;
                lock (_sync)
                {
                    if (_disposed || epoch != _accountEpoch) return;
                    _connection = connection;
                    unattached = false;
                    _readingInitialAccount = true;
                    _accountKnownOnConnection = false;
                    var source = connection;
                    _notificationHandler = (method, payload) => OnNotification(source, method, payload);
                    connection.Notification += _notificationHandler;
                }
            }
            stage = "account/read";
            var account = await RequestAsync(connection, "account/read", new { refreshToken = false }).ConfigureAwait(false);
            lock (_sync)
            {
                _readingInitialAccount = false;
                if (_disposed || epoch != _accountEpoch) return;
            }
            if (!account.TryGetProperty("account", out var identity)) throw new IOException("Account information is unavailable.");
            if (identity.ValueKind == JsonValueKind.Null)
            {
                lock (_sync) { _signedOut = true; _accountIdentity = null; ClearKnownQuota(); }
                Publish(new(QuotaStatus.NotLoggedIn, null, null, _clock()), epoch, success: true);
                return;
            }
            if (identity.ValueKind != JsonValueKind.Object) throw new IOException("Account information is unavailable.");
            var accountIdentity = GetAccountIdentity(identity);
            long revision;
            QuotaSnapshot? cleared = null;
            lock (_sync)
            {
                if (_disposed || epoch != _accountEpoch) return;
                // A connection may have been idle while the desktop account changed.
                // Retain old data on errors only after a comparable identity matches.
                if (accountIdentity is null || accountIdentity != _accountIdentity)
                {
                    if (accountIdentity is null || accountIdentity != _lastKnownIdentity) ClearKnownQuota();
                    _lastSuccess = null;
                    if (accountIdentity != _accountIdentity)
                    {
                        _observedResets.Clear();
                        _scheduledResets.Clear();
                        _resetPending = false;
                    }
                    _quotaRevision++;
                    if (_snapshot.Status != QuotaStatus.Loading || _snapshot.FiveHour is not null || _snapshot.Weekly is not null)
                        cleared = _snapshot = new(QuotaStatus.Loading, null, null, null);
                }
                _accountIdentity = accountIdentity;
                if (accountIdentity is not null && accountIdentity == _lastKnownIdentity &&
                    _lastKnownQuota is { } saved && _snapshot.FiveHour is null && _snapshot.Weekly is null)
                    cleared = _snapshot = saved with { Status = QuotaStatus.Stale };
                _signedOut = false;
                _accountKnownOnConnection = true;
                identityVerified = accountIdentity is not null;
                revision = _quotaRevision;
            }
            if (cleared is not null) RaiseUpdated(cleared);
            stage = "account/rateLimits/read";
            var response = await RequestAsync(connection, "account/rateLimits/read", new { }).ConfigureAwait(false);
            lock (_sync) { if (_disposed || epoch != _accountEpoch || revision != _quotaRevision) return; }
            var parsed = QuotaParser.Parse(response, _clock());
            if (parsed.Status != QuotaStatus.Live) throw new IOException("Quota information is unavailable.");
            Publish(parsed, epoch, success: true, revision);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Raw process/RPC errors can contain tokens or account data.
            QuotaSnapshot? failed = null;
            ICodexConnection? disconnected = null;
            lock (_sync)
            {
                if (_disposed || epoch != _accountEpoch) return;
                if (connection is not null && ReferenceEquals(_connection, connection) &&
                    (error is TimeoutException or OperationCanceledException || !connection.IsConnected))
                    disconnected = DetachConnection();
                // A newer authenticated push is already the successful outcome.
                // An older request failure must not erase it or re-enable backoff.
                if (_quotaRevision == startedRevision || _snapshot.Status != QuotaStatus.Live)
                {
                    if (!identityVerified)
                    {
                        _snapshot = new(QuotaStatus.Loading, null, null, null);
                        _lastSuccess = null;
                    }
                    _failures++;
                    _retryAt = _clock().AddSeconds(_failures == 1 ? 5 : _failures == 2 ? 15 : 60);
                    failed = _snapshot = _snapshot with
                    {
                        Status = _snapshot.FiveHour is not null || _snapshot.Weekly is not null ? QuotaStatus.Stale : QuotaStatus.Unavailable,
                        Error = "Codex quota is temporarily unavailable (" + stage + ": " + error.GetType().Name + ").",
                        FailureKind = error is TimeoutException or OperationCanceledException ? QuotaFailureKind.Timeout
                            : stage == "connect" || connection is { IsConnected: false } ? QuotaFailureKind.Connection : QuotaFailureKind.InvalidResponse,
                        RetryAt = _retryAt
                    };
                }
            }
            if (disconnected is not null) await SafeDisposeAsync(disconnected).ConfigureAwait(false);
            if (failed is not null && ReferenceEquals(Snapshot, failed)) RaiseUpdated(failed);
        }
        finally
        {
            // If shutdown overtook connection creation, this client still owns it.
            bool dispose;
            lock (_sync)
            {
                _readingInitialAccount = false;
                if (ReferenceEquals(connection, _connection)) _connectionUsed = _clock();
                dispose = unattached;
            }
            if (dispose && connection is not null) try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
            completion.TrySetResult(Snapshot);
            Wake();
        }
    }

    // These helpers only release this service's own transport/process.
    ICodexConnection? DetachConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null && _notificationHandler is not null) connection.Notification -= _notificationHandler;
        _notificationHandler = null;
        return connection;
    }

    static async Task SafeDisposeAsync(ICodexConnection connection)
    { try { await connection.DisposeAsync().ConfigureAwait(false); } catch { } }

    static async Task DisposeLateConnectionAsync(Task<ICodexConnection> connecting)
    { try { await SafeDisposeAsync(await connecting.ConfigureAwait(false)).ConfigureAwait(false); } catch { } }

    void ClearKnownQuota()
    { _lastKnownQuota = null; _lastKnownIdentity = null; _resetTracker.Clear(); _lastResetNotice = null; }
    QuotaResetNotice? RememberQuota(QuotaSnapshot snapshot)
    {
        if (snapshot.Status == QuotaStatus.Live && _accountIdentity is not null)
        { _lastKnownQuota = snapshot; _lastKnownIdentity = _accountIdentity; }
        var notice = _resetTracker.Observe(_accountIdentity, snapshot);
        if (notice is not null) _lastResetNotice = notice;
        return notice;
    }

    static string? GetAccountIdentity(JsonElement account)
    {
        foreach (var name in new[] { "id", "accountId", "email" })
            if (account.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return (account.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : "") + ":" + name + ":" + value.GetString();
        return null;
    }

    async Task<JsonElement> RequestAsync(ICodexConnection connection, string method, object parameters)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await connection.RequestAsync(method, parameters, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        { throw new TimeoutException("Codex read deadline elapsed."); }
    }

    void Publish(QuotaSnapshot snapshot, long epoch, bool success, long? revision = null)
    {
        QuotaResetNotice? notice;
        lock (_sync)
        {
            if (_disposed || epoch != _accountEpoch || revision is not null && revision != _quotaRevision) return;
            ObserveElapsedResets(_clock());
            _snapshot = snapshot;
            notice = RememberQuota(snapshot);
            if (success)
            {
                TrackResets(snapshot, _refreshStartedAt);
                _lastSuccess = _clock();
                _nextPeriodic = _lastSuccess.Value + _refreshInterval;
                _retryAt = DateTimeOffset.MinValue;
                _failures = 0;
            }
        }
        RaiseUpdated(snapshot);
        RaiseReset(notice);
        Wake();
    }

    void RaiseReset(QuotaResetNotice? notice)
    {
        if (notice is null || !IsCurrentResetNotice(notice)) return;
        foreach (var handler in ResetDetected?.GetInvocationList() ?? [])
            try { ((Action<QuotaResetNotice>)handler)(notice); } catch { }
    }

    void RaiseUpdated(QuotaSnapshot snapshot)
    {
        foreach (var handler in Updated?.GetInvocationList() ?? [])
            try { ((Action<QuotaSnapshot>)handler)(snapshot); } catch { }
    }

    void OnNotification(ICodexConnection source, string method, JsonElement payload)
    {
        QuotaSnapshot? changed = null;
        QuotaResetNotice? notice = null;
        bool refetch = false;
        long epoch;
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(source, _connection)) return;
            if (method == "account/updated")
            {
                var signedOut = payload.TryGetProperty("authMode", out var mode) && mode.ValueKind == JsonValueKind.Null;
                // A new stdio server announces its current sign-in during the first
                // account/read. That response already resolves the announced account.
                var initialAccount = _readingInitialAccount && !signedOut;
                _readingInitialAccount = false;
                if (!initialAccount)
                {
                    _accountEpoch++;
                    _quotaRevision++;
                    _lastSuccess = null;
                    _accountIdentity = null;
                    ClearKnownQuota();
                    _observedResets.Clear();
                    _scheduledResets.Clear();
                    _resetPending = false;
                    _failures = 0;
                }
                _accountKnownOnConnection = false;
                _signedOut = signedOut;
                changed = _snapshot = initialAccount && (_snapshot.FiveHour is not null || _snapshot.Weekly is not null)
                    ? _snapshot with { Status = QuotaStatus.Stale }
                    : new(_signedOut ? QuotaStatus.NotLoggedIn : QuotaStatus.Loading, null, null, null);
                refetch = !_signedOut && !initialAccount;
                if (!initialAccount) _nextPeriodic = _signedOut ? _clock() + _refreshInterval : _clock();
            }
            else if (method == "account/rateLimits/updated" && !_signedOut && _accountKnownOnConnection)
            {
                var parsed = QuotaParser.Parse(payload, _clock());
                // Rolling notifications are sparse. Merge only observed windows;
                // missing data must remain unknown until a real snapshot arrives.
                if (parsed.FiveHour is not null || parsed.Weekly is not null)
                {
                    ObserveElapsedResets(_clock());
                    _quotaRevision++;
                    changed = _snapshot = new(QuotaStatus.Live, parsed.FiveHour ?? _snapshot.FiveHour,
                        parsed.Weekly ?? _snapshot.Weekly, _clock());
                    notice = RememberQuota(changed);
                    _lastSuccess = _clock();
                    TrackResets(_snapshot);
                    _failures = 0;
                    _nextPeriodic = _lastSuccess.Value + _refreshInterval;
                    _retryAt = DateTimeOffset.MinValue;
                }
                refetch = parsed.FiveHour is null || parsed.Weekly is null;
            }
            epoch = _accountEpoch;
            if (refetch) _notificationPending = true;
        }
        if (changed is not null) RaiseUpdated(changed);
        RaiseReset(notice);
        if (refetch) _ = RefetchNotificationAsync(epoch);
        Wake();
    }

    async Task RefetchNotificationAsync(long epoch)
    {
        try
        {
            await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            Task<QuotaSnapshot>? current;
            lock (_sync) { if (_disposed || epoch != _accountEpoch) return; current = _refresh; }
            if (current is not null) await current.WaitAsync(_stop.Token).ConfigureAwait(false);
            lock (_sync) { if (_disposed || epoch != _accountEpoch || !_notificationPending) return; }
            await RequestRefresh(true, scheduled: true, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    void TrackResets(QuotaSnapshot snapshot, DateTimeOffset? readStartedAt = null)
    {
        foreach (var window in new[] { snapshot.FiveHour, snapshot.Weekly })
        {
            if (window?.ResetsAt is not { } reset) continue;
            var timestamp = reset.ToUnixTimeSeconds();
            if (readStartedAt is { } started && reset <= started)
            {
                _observedResets.Add(timestamp);
                _scheduledResets.Remove(timestamp);
            }
            else if (!_observedResets.Contains(timestamp)) _scheduledResets.Add(timestamp);
        }
    }

    void ObserveElapsedResets(DateTimeOffset now)
    {
        foreach (var timestamp in _scheduledResets.Where(timestamp => timestamp <= now.ToUnixTimeSeconds()).ToArray())
        {
            _scheduledResets.Remove(timestamp);
            if (_observedResets.Add(timestamp)) _resetPending = true;
        }
    }

    DateTimeOffset NetworkDeadline(DateTimeOffset now)
        => _failures > 0 ? _retryAt : _prefetchPending || _resetPending || _notificationPending ? now : _nextPeriodic;

    TimeSpan NextDelay(DateTimeOffset now, bool refreshing)
    {
        var deadline = refreshing ? DateTimeOffset.MaxValue : NetworkDeadline(now);
        if (!refreshing && _connection is not null)
        {
            var idleAt = _visible ? _connectionUsed.AddSeconds(10) : now;
            if (idleAt < deadline) deadline = idleAt;
        }
        foreach (var timestamp in _scheduledResets)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            if (resetAt < deadline) deadline = resetAt;
        }
        if (deadline == DateTimeOffset.MaxValue) return Timeout.InfiniteTimeSpan;
        if (deadline <= now) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(Math.Min((deadline - now).TotalMilliseconds, int.MaxValue - 1));
    }

    async Task PollAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan delay;
                bool due;
                ICodexConnection? idle = null;
                lock (_sync)
                {
                    if (_disposed) return;
                    var now = _clock();
                    ObserveElapsedResets(now);
                    var refreshing = _refresh is { IsCompleted: false };
                    if (_connection is not null && !refreshing &&
                        (!_visible || now - _connectionUsed >= TimeSpan.FromSeconds(10)))
                    { idle = DetachConnection(); }
                    due = !refreshing && now >= NetworkDeadline(now);
                    delay = NextDelay(now, refreshing);
                }
                if (idle is not null) await SafeDisposeAsync(idle).ConfigureAwait(false);
                if (due)
                {
                    _ = RequestRefresh(true, scheduled: true, _stop.Token);
                    continue;
                }
                await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }

    public async ValueTask DisposeAsync()
    {
        ICodexConnection? connection;
        Task? loop;
        Task<QuotaSnapshot>? refresh;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            connection = DetachConnection();
            loop = _loop;
            refresh = _refresh;
        }
        _stop.Cancel();
        if (connection is not null)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (loop is not null) await loop.ConfigureAwait(false);
        if (refresh is not null) await refresh.ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }
}
