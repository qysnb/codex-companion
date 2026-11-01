namespace CodexCompanion.Core;
public sealed partial class QuotaService
{
    bool _localEnabled;
    QuotaSnapshot? _localSnapshot;
    DateTimeOffset? _localObservedAt;
    long _localGeneration;
    DateTimeOffset? _localReadAt, _remoteReadAt;
    long _readSequence, _localReadSequence, _remoteReadSequence;
    /// <summary>Latest successful read across sources, separate from quota data freshness.</summary>
    public DateTimeOffset? LastReadAt
    {
        get
        {
            lock (_sync)
            {
                var local = _localEnabled && _localSnapshot is not null ? _localReadAt : null;
                var remote = _snapshot.FiveHour is not null || _snapshot.Weekly is not null ? _remoteReadAt : null;
                return local is null ? remote : remote is null || local >= remote ? local : remote;
            }
        }
    }
    QuotaSnapshot CurrentSnapshot()
        => _localEnabled && _localSnapshot is { UpdatedAt: { } timestamp } local &&
            (_snapshot.FiveHour is null && _snapshot.Weekly is null || _snapshot.UpdatedAt is null || timestamp > _snapshot.UpdatedAt ||
                timestamp == _snapshot.UpdatedAt && (_localReadAt > _remoteReadAt || _localReadAt == _remoteReadAt && _localReadSequence > _remoteReadSequence))
            ? local : _snapshot;
    bool LocalPreferred() => _localSnapshot is not null && ReferenceEquals(CurrentSnapshot(), _localSnapshot);
    bool LocalFresh(DateTimeOffset now) => LocalPreferred() && _localObservedAt is { } observed && now < observed + _refreshInterval;
    public bool LocalSyncEnabled
    {
        get { lock (_sync) return _localEnabled; }
        set
        {
            QuotaSnapshot snapshot;
            lock (_sync)
            {
                if (_disposed || _localEnabled == value) return;
                _localEnabled = value;
                if (!value) { _localSnapshot = null; _localObservedAt = _localReadAt = null; }
                snapshot = CurrentSnapshot();
            }
            RaiseUpdated(snapshot); Wake();
        }
    }
    public string Source
    { get { lock (_sync) return LocalPreferred() ? "local-session" : _snapshot.FiveHour is not null || _snapshot.Weekly is not null ? "app-server" : "none"; } }
    public bool AcceptLocalReading(LocalQuotaReading reading)
    {
        QuotaSnapshot snapshot;
        lock (_sync)
        {
            if (_disposed || !_localEnabled || reading.Generation < _localGeneration ||
                reading.Snapshot.Status != QuotaStatus.Live || reading.Snapshot.UpdatedAt is not { } time ||
                reading.Snapshot.FiveHour is null && reading.Snapshot.Weekly is null ||
                reading.Generation == _localGeneration && _localSnapshot?.UpdatedAt > time) return false;
            if (reading.IsRecheck && (reading.Generation != _localGeneration || reading.Snapshot != _localSnapshot)) return false;
            _localGeneration = reading.Generation;
            _localSnapshot = reading.Snapshot;
            if (_localReadAt is null || reading.ObservedAt >= _localReadAt)
            { _localReadAt = reading.ObservedAt; _localReadSequence = ++_readSequence; }
            // A delayed local batch is only as fresh as its original event.
            _localObservedAt = time < reading.ObservedAt ? time : reading.ObservedAt;
            snapshot = CurrentSnapshot();
            // Session events have no verified account identity. Never merge them
            // with the private connection's account cache or infer reset notices.
            if (LocalPreferred()) { _resetTracker.Clear(); _lastResetNotice = null; }
        }
        RaiseUpdated(snapshot); Wake(); return true;
    }
    public void InvalidateLocalReading(long generation, bool accountChanged)
    {
        QuotaSnapshot snapshot;
        lock (_sync)
        {
            if (_disposed || generation < _localGeneration) return;
            _localGeneration = generation; _localSnapshot = null; _localObservedAt = _localReadAt = null;
            if (accountChanged)
            {
                _accountEpoch++; _quotaRevision++; _accountIdentity = null; _accountKnownOnConnection = false;
                ClearKnownQuota(); _lastSuccess = null; _lastAttempt = null; _failures = 0;
                _scheduledResets.Clear(); _observedResets.Clear(); _resetPending = _notificationPending = false;
                _prefetchPending = _networkMode == QuotaNetworkMode.Automatic;
                _nextPeriodic = _clock(); _retryAt = DateTimeOffset.MinValue;
                _snapshot = new(QuotaStatus.Loading, null, null, null);
            }
            snapshot = CurrentSnapshot();
        }
        RaiseUpdated(snapshot); Wake();
    }
    void MarkRemoteRead(QuotaSnapshot snapshot)
    {
        if (snapshot.Status == QuotaStatus.Live && (snapshot.FiveHour is not null || snapshot.Weekly is not null))
        { _remoteReadAt = _clock(); _remoteReadSequence = ++_readSequence; }
    }
}
