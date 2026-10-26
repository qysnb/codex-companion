namespace CodexCompanion.Core;

public static class CodexRequestPolicy
{
    // Account initialization and the read-only rate-limit endpoint can complete
    // successfully beyond 10s. Both transport and service must share this budget:
    // an earlier inner timeout would otherwise silently defeat the outer one.
    public static TimeSpan TimeoutFor(string method) => method is "account/read" or "account/rateLimits/read"
        ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);
}
