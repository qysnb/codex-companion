using System.Text.Json;
namespace CodexCompanion.Core;

public sealed record LocalQuotaReading(QuotaSnapshot Snapshot, long Generation, DateTimeOffset ObservedAt);
public static class LocalQuotaRecordParser
{
    // Inspect only the fixed envelope before permitting JSON deserialization.
    // Message/tool records are rejected before their bodies are buffered.
    internal static int Header(ReadOnlySpan<byte> prefix)
    {
        try
        {
            var reader = new Utf8JsonReader(prefix, false, default);
            if (!reader.Read()) return 0;
            if (reader.TokenType != JsonTokenType.StartObject) return -1;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return -1;
                var type = reader.ValueTextEquals("type"u8);
                var payload = reader.ValueTextEquals("payload"u8);
                if (!reader.Read()) return 0;
                if (type && (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("event_msg"u8))) return -1;
                if (payload)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) return -1;
                    if (!reader.Read()) return 0;
                    if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("type"u8)) return -1;
                    if (!reader.Read()) return 0;
                    return reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("token_count"u8) ? 1 : -1;
                }
                if (!reader.TrySkip()) return 0;
            }
            return 0;
        }
        catch (JsonException) { return -1; }
    }
    public static QuotaSnapshot? Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length > 8192 || Header(record) != 1) return null;
        try
        {
            var reader = new Utf8JsonReader(record);
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return null;
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_msg" ||
                !root.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var time) ||
                !root.TryGetProperty("payload", out var payload) || payload.GetProperty("type").GetString() != "token_count" ||
                !payload.TryGetProperty("rate_limits", out var rate) || rate.ValueKind != JsonValueKind.Object) return null;
            if (rate.TryGetProperty("limit_id", out var id) && id.ValueKind != JsonValueKind.Null && id.GetString() != "codex") return null;
            QuotaWindow? five = null, week = null;
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!rate.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("used_percent", out var used) || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) ||
                    !window.TryGetProperty("window_minutes", out var duration) || !duration.TryGetInt32(out var minutes)) continue;
                DateTimeOffset? reset = null;
                if (window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var unix))
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
                var value = new QuotaWindow(Math.Clamp(100 - percent, 0, 100), minutes, reset);
                if (minutes == 300) five = value;
                if (minutes == 10080) week = value;
            }
            return five is null && week is null ? null : new(QuotaStatus.Live, five, week, time);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
}
public sealed class LocalQuotaDecoder
{
    readonly byte[] buffer = new byte[8192];
    int length;
    bool candidate, discard;
    public int CapacityBytes => 8192;
    public int BufferedBytes => length;
    public void Reset(bool skipPartial = false) { length = 0; candidate = false; discard = skipPartial; }
    public void Feed(ReadOnlySpan<byte> bytes, Action<QuotaSnapshot> reading)
    {
        foreach (var b in bytes)
        {
            if (b == '\n')
            {
                if (!discard && length > 0 && LocalQuotaRecordParser.Parse(buffer.AsSpan(0, length)) is { } snapshot) reading(snapshot);
                Reset(); continue;
            }
            if (discard) continue;
            if (length == buffer.Length) { length = 0; discard = true; continue; }
            buffer[length++] = b;
            if (!candidate && length >= 32 && length % 16 == 0)
            {
                var header = LocalQuotaRecordParser.Header(buffer.AsSpan(0, length));
                if (header == 1) candidate = true;
                else if (header == -1 || length >= 192) { length = 0; discard = true; }
            }
        }
    }
}
