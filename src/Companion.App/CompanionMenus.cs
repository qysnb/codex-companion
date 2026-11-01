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
        menu.Items.Add(SettingsItem(Local("本地自动同步", "Automatic local sync", "ローカル自動同期"), "local-sync", ToggleLocalQuotaSync, settings.LocalQuotaSync,
            Local("同步宿主新事件并复核本地额度记录，不额外联网。没有新数据时保留额度值，成功读取时更新时间。", "Follow host events and reread the local quota record without extra network requests. Keep quota values when no new data arrives and update the time after successful reads.", "ホストの新しいイベントと同期し、ローカル記録を再読取します。追加通信は行わず、データが変わらなくても読取成功時に日時を更新します。")));
        if (settings.LocalQuotaSync)
        {
            var interval = SettingsItem(Local("本地同步间隔 · ", "Local interval · ", "ローカル同期の間隔 · ") + LocalSyncIntervalLabel(settings.LocalQuotaSyncIntervalMs), "local-interval", hint:
                Local("即时模式跟随新事件；固定间隔内合并更新，只读取最新额度。仅影响本地读取，不会联网。", "Immediate follows new events; fixed intervals combine updates into the newest reading. Local reads only, no network requests.", "即時は新しいイベントに追従し、固定間隔では更新を最新の値にまとめます。ローカル読取のみで通信しません。"));
            var presets = new[] { 0, 500, 1000, 3000, 5000, 10000, 30000, 60000 };
            foreach (var milliseconds in presets)
                interval.Items.Add(SettingsItem(LocalSyncIntervalLabel(milliseconds), "local-interval-" + milliseconds, () => SetLocalSyncInterval(milliseconds), settings.LocalQuotaSyncIntervalMs == milliseconds));
            interval.Items.Add(new Separator());
            var custom = !presets.Contains(settings.LocalQuotaSyncIntervalMs);
            interval.Items.Add(SettingsItem(Local("自定义…", "Custom…", "カスタム…") + (custom ? " (" + LocalSyncIntervalLabel(settings.LocalQuotaSyncIntervalMs) + ")" : ""), "local-interval-custom", ConfigureLocalSyncInterval, custom));
            menu.Items.Add(interval);
        }
        var state = !settings.LocalQuotaSync ? "paused" : subscription?.State is "unavailable" or "stopped" ? "unavailable"
            : subscription is { Readings: > 0, LastReceivedAt: not null } ? "received" : "waiting";
        var stateLabel = state switch {
            "paused" => Local("已暂停", "Paused", "一時停止中"),
            "unavailable" => Local("本地数据暂不可用", "Local data unavailable", "ローカルデータを取得できません"),
            "received" => Local("已收到宿主更新", "Host updates received", "ホストの更新を受信済み"),
            _ => Local("等待宿主更新", "Waiting for host updates", "ホストの更新を待機中") };
        var status = SettingsItem(Local("同步状态：", "Sync: ", "同期状態：") + stateLabel, "sync-status-" + state);
        status.IsEnabled = false; menu.Items.Add(status);
        if (settings.LocalQuotaSync && (subscription?.LastReadAt ?? subscription?.LastReceivedAt) is { } received)
        {
            var time = SettingsItem(Local("最近读取：", "Last read: ", "最終読取：") + received.ToLocalTime().ToString("MM-dd HH:mm:ss"), "last-local-sync");
            time.IsEnabled = false; menu.Items.Add(time);
        }
        menu.Items.Add(new Separator());
        var network = SettingsItem(Local("联网补充方式", "Online supplements", "オンライン補完"), "network-mode");
        foreach (var (value, label, hint) in new[] {
            ("local", Local("仅手动联网（推荐）", "Manual only (recommended)", "手動のみ（推奨）"),
                Local("不自动联网，仅“联网刷新一次”会发起额度查询。本地同步由上方开关控制。", "No automatic network access. Only Refresh online once requests quota online; the switch above controls local sync.", "自動通信は行いません。「一度オンライン更新」のみ通信し、ローカル同期は上のスイッチで設定します。")),
            ("on-demand", Local("按需联网", "On demand", "必要時のみ"),
                Local("查看额度时可补充查询，尝试至少相隔15分钟；本地数据新鲜时不查询。", "Opening quota may fetch a supplement, at least 15 minutes between attempts. Fresh local data avoids a request.", "利用枠の表示時に補完します。試行は15分以上の間隔とし、ローカルデータが新しければ通信しません。")),
            ("automatic", Local("定时联网补充", "Scheduled supplements", "定期的にオンライン補完"),
                Local("本地数据新鲜时不查询；需要补充时按间隔联网，可能产生额外流量。", "Fresh local data avoids a query. Otherwise supplement quota on a schedule, which can use extra data.", "ローカルデータが新しければ通信しません。必要に応じて指定間隔で補完し、追加の通信量が発生する場合があります。")) })
            network.Items.Add(SettingsItem(label, "network-" + value, () => SetQuotaNetworkMode(value), settings.QuotaNetworkMode == value, hint));
        menu.Items.Add(network);
        if (settings.QuotaNetworkMode == "automatic")
        {
            var refresh = SettingsItem(Local("联网补充间隔 · ", "Online interval · ", "オンライン補完の間隔 · ") + RefreshIntervalLabel(settings.RefreshIntervalSeconds), "online-interval");
            var presets = new[] { 60, 300, 900, 1800, 3600 };
            foreach (var seconds in presets)
                refresh.Items.Add(SettingsItem(RefreshIntervalLabel(seconds), "interval-" + seconds, () => SetRefreshInterval(seconds), settings.RefreshIntervalSeconds == seconds));
            refresh.Items.Add(new Separator());
            var custom = !presets.Contains(settings.RefreshIntervalSeconds);
            refresh.Items.Add(SettingsItem(Local("自定义…", "Custom…", "カスタム…") + (custom ? " (" + RefreshIntervalLabel(settings.RefreshIntervalSeconds) + ")" : ""), "interval-custom", ConfigureRefreshInterval, custom));
            menu.Items.Add(refresh);
        }
        menu.Items.Add(SettingsItem(Local("联网刷新一次", "Refresh online once", "一度オンライン更新"), "refresh-online", () => _ = quota.RefreshAsync(true), hint:
            Local("立即向服务端请求额度，数据返回后更新。此操作需要网络。", "Request quota from the service and update when it arrives. Requires network access.", "サービスに利用枠を問い合わせ、応答後に更新します。通信が必要です。")));
        menu.Items.Add(SettingsItem(Local("额度恢复提醒", "Quota recovery reminders", "利用枠の回復通知"), "quota-reminders", () => {
            settings = settings with { QuotaResetNotifications = !settings.QuotaResetNotifications };
            if (!settings.QuotaResetNotifications) { resetReminder?.Close(); resetReminder = null; }
            Save();
        }, settings.QuotaResetNotifications));
        return menu;
    }
}
