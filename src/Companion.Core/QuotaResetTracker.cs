namespace CodexCompanion.Core;

public sealed record QuotaResetNotice(bool FiveHour, bool Weekly);

/// <summary>Observes real, account-verified readings only; no polling or inferred quota.</summary>
public sealed class QuotaResetTracker
{
    string? _identity;
    double? _fiveHour, _weekly;
    public QuotaResetNotice? Observe(string? identity, QuotaSnapshot snapshot)
    {
        if (identity is null || identity != _identity) Clear();
        _identity = identity;
        if (identity is null || snapshot.Status != QuotaStatus.Live) return null;
        var five = snapshot.FiveHour?.RemainingPercent;
        var week = snapshot.Weekly?.RemainingPercent;
        var fiveReset = _fiveHour == 0 && five == 100;
        var weekReset = _weekly == 0 && week == 100;
        if (five is not null) _fiveHour = five;
        if (week is not null) _weekly = week;
        return fiveReset || weekReset ? new(fiveReset, weekReset) : null;
    }
    public void Clear() { _identity = null; _fiveHour = _weekly = null; }
}
