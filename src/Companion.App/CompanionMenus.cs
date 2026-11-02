using System.Windows;
using System.Windows.Controls;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal sealed partial class CompanionWindow
{
    MenuItem SettingsItem(string label, string id, Action? action = null, bool? check = null, string? hint = null)
    {
        var item = new MenuItem { Header = label, Tag = id, IsCheckable = check.HasValue, IsChecked = check == true, ToolTip = hint };
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }
    string RefreshIntervalLabel(int seconds) => seconds % 60 == 0
        ? seconds / 60 + Local(" 分钟", " min", " 分") : seconds + Local(" 秒", " sec", " 秒");
    string LocalSyncIntervalLabel(int milliseconds) => milliseconds == 0 ? Local("即时（推荐）", "Immediate (recommended)", "即時（推奨）")
        : milliseconds % 1000 == 0 ? RefreshIntervalLabel(milliseconds / 1000)
        : (milliseconds / 1000m).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) + Local(" 秒", " sec", " 秒");

    MenuItem BuildQuotaSettingsMenu(LocalQuotaMonitorStats? subscription)
    {
        var menu = SettingsItem(Local("额度设置", "Quota settings", "利用枠の設定"), "quota-settings");
        var network = SettingsItem(Local("读取方式 · ", "Query mode · ", "取得方法 · ") + QuotaModeLabel(settings.QuotaNetworkMode), "network-mode");
        foreach (var (value, hint) in new[] {
            ("automatic", Local("按所设间隔优先通过 HTTP 查询；特定失败时自动限频使用兼容查询。本地事件不会阻止定时查询。", "Query HTTP quota on schedule. Certain failures use rate-limited compatibility queries; local events do not delay scheduled queries.", "指定間隔で HTTP 取得します。特定の失敗時のみ頻度を制限して互換取得し、ローカルイベントは定期取得を妨げません。")),
            ("on-demand", Local("查看额度时可通过 HTTP 查询，尝试至少相隔15分钟；不进行定时查询。", "Opening quota may query HTTP, with at least 15 minutes between attempts. No scheduled queries.", "利用枠の表示時に HTTP 取得します。試行間隔は15分以上で、定期取得は行いません。")),
            ("local", Local("只在点击“立即刷新额度”时联网。本地事件由“本地事件辅助”单独控制。", "Only Refresh quota now requests online data. Local events are controlled separately under Local event support.", "「今すぐ利用枠を更新」のみ通信します。ローカルイベントは別の補助設定で切り替えます。")) })
            network.Items.Add(SettingsItem(QuotaModeLabel(value), "network-" + value, () => SetQuotaNetworkMode(value), settings.QuotaNetworkMode == value, hint));
        menu.Items.Add(network);
        if (settings.QuotaNetworkMode == "automatic")
        {
            var refresh = SettingsItem(Local("查询间隔 · ", "Query interval · ", "取得間隔 · ") + RefreshIntervalLabel(settings.RefreshIntervalSeconds), "online-interval");
            var presets = new[] { 30, 60, 300, 900, 1800, 3600 };
            foreach (var seconds in presets)
                refresh.Items.Add(SettingsItem(RefreshIntervalLabel(seconds), "interval-" + seconds, () => SetRefreshInterval(seconds), settings.RefreshIntervalSeconds == seconds));
            refresh.Items.Add(new Separator());
            var custom = !presets.Contains(settings.RefreshIntervalSeconds);
            refresh.Items.Add(SettingsItem(Local("自定义…", "Custom…", "カスタム…") + (custom ? " (" + RefreshIntervalLabel(settings.RefreshIntervalSeconds) + ")" : ""), "interval-custom", ConfigureRefreshInterval, custom));
            menu.Items.Add(refresh);
        }
        menu.Items.Add(SettingsItem(Local("立即刷新额度", "Refresh quota now", "今すぐ利用枠を更新"), "refresh-online", () => _ = quota.RefreshAsync(true), hint:
            Local("立即向服务端请求额度，数据返回后更新。此操作需要网络。", "Request quota from the service and update when it arrives. Requires network access.", "サービスに利用枠を問い合わせ、応答後に更新します。通信が必要です。")));
        menu.Items.Add(new Separator());
        AddQuotaReadStatus(menu, quota.Source, quota.LastReadAt);
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildLocalQuotaOptions(subscription));
        menu.Items.Add(SettingsItem(Local("额度恢复提醒", "Quota recovery reminders", "利用枠の回復通知"), "quota-reminders", () => {
            settings = settings with { QuotaResetNotifications = !settings.QuotaResetNotifications };
            if (!settings.QuotaResetNotifications) { resetReminder?.Close(); resetReminder = null; }
            Save();
        }, settings.QuotaResetNotifications));
        return menu;
    }
    string QuotaModeLabel(string mode) => mode switch {
        "automatic" => Local("定时查询", "Scheduled queries", "定期取得"),
        "on-demand" => Local("查看时查询", "Query when opened", "表示時に取得"),
        _ => Local("仅手动查询", "Manual queries only", "手動取得のみ") };

    void AddQuotaReadStatus(MenuItem menu, string source, DateTimeOffset? readAt)
    {
        var label = source switch {
            "http" => Local("在线查询", "Online query", "オンライン取得"),
            "app-server" => Local("兼容查询", "Compatibility query", "互換取得"),
            "local-session" => Local("本地事件", "Local events", "ローカルイベント"),
            _ => Local("暂无数据", "No data yet", "データ未取得") };
        var sourceItem = SettingsItem(Local("当前来源：", "Source: ", "現在の取得元：") + label, "quota-source");
        sourceItem.IsEnabled = false; menu.Items.Add(sourceItem);
        var timeItem = SettingsItem(Local("最近读取：", "Last read: ", "最終読取：") + (readAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—"), "quota-last-read");
        timeItem.IsEnabled = false; menu.Items.Add(timeItem);
    }

    MenuItem BuildLocalQuotaOptions(LocalQuotaMonitorStats? subscription)
    {
        var local = SettingsItem(Local("本地事件辅助", "Local event support", "ローカルイベント補助"), "local-options", hint:
            Local("读取宿主写出的本地额度事件，无额外流量。定时查询优先采用 HTTP 结果；此开关不影响联网查询。", "Read host quota events without extra traffic. Scheduled queries prioritize HTTP results; this switch does not change online queries.", "ホストの利用枠イベントを追加通信なしで読み取ります。定期取得では HTTP 結果を優先し、このスイッチはオンライン取得に影響しません。"));
        local.Items.Add(SettingsItem(Local("启用本地事件", "Enable local events", "ローカルイベントを有効化"), "local-sync", ToggleLocalQuotaSync, settings.LocalQuotaSync,
            Local("同步宿主新事件并复核本地额度记录，不额外联网。没有新数据时保留额度值，成功读取时更新时间。", "Follow host events and reread the local quota record without extra network requests. Keep quota values when no new data arrives and update the time after successful reads.", "ホストの新しいイベントと同期し、ローカル記録を再読取します。追加通信は行わず、データが変わらなくても読取成功時に日時を更新します。")));
        if (settings.LocalQuotaSync)
        {
            var interval = SettingsItem(Local("事件读取间隔 · ", "Event read interval · ", "イベント読取間隔 · ") + LocalSyncIntervalLabel(settings.LocalQuotaSyncIntervalMs), "local-interval", hint:
                Local("即时模式跟随新事件；固定间隔内合并更新，只读取最新额度。仅影响本地读取，不会联网。", "Immediate follows new events; fixed intervals combine updates into the newest reading. Local reads only, no network requests.", "即時は新しいイベントに追従し、固定間隔では更新を最新の値にまとめます。ローカル読取のみで通信しません。"));
            var presets = new[] { 0, 500, 1000, 3000, 5000, 10000, 30000, 60000 };
            foreach (var milliseconds in presets)
                interval.Items.Add(SettingsItem(LocalSyncIntervalLabel(milliseconds), "local-interval-" + milliseconds, () => SetLocalSyncInterval(milliseconds), settings.LocalQuotaSyncIntervalMs == milliseconds));
            interval.Items.Add(new Separator());
            var custom = !presets.Contains(settings.LocalQuotaSyncIntervalMs);
            interval.Items.Add(SettingsItem(Local("自定义…", "Custom…", "カスタム…") + (custom ? " (" + LocalSyncIntervalLabel(settings.LocalQuotaSyncIntervalMs) + ")" : ""), "local-interval-custom", ConfigureLocalSyncInterval, custom));
            local.Items.Add(interval);
        }
        var state = !settings.LocalQuotaSync ? "paused" : subscription?.State is "unavailable" or "stopped" ? "unavailable"
            : subscription is { Readings: > 0, LastReceivedAt: not null } ? "received" : "waiting";
        var stateLabel = state switch {
            "paused" => Local("已暂停", "Paused", "一時停止中"),
            "unavailable" => Local("本地数据暂不可用", "Local data unavailable", "ローカルデータを取得できません"),
            "received" => Local("已收到本地事件", "Local events received", "ローカルイベントを受信済み"),
            _ => Local("等待宿主更新", "Waiting for host updates", "ホストの更新を待機中") };
        var status = SettingsItem(Local("同步状态：", "Sync: ", "同期状態：") + stateLabel, "sync-status-" + state);
        status.IsEnabled = false; local.Items.Add(status);
        if (settings.LocalQuotaSync && (subscription?.LastReadAt ?? subscription?.LastReceivedAt) is { } received)
        {
            var time = SettingsItem(Local("本地读取：", "Local read: ", "ローカル読取：") + received.ToLocalTime().ToString("MM-dd HH:mm:ss"), "last-local-sync");
            time.IsEnabled = false; local.Items.Add(time);
        }
        return local;
    }

}
