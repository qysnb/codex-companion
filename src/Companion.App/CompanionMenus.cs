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

    MenuItem BuildQuotaSettingsMenu(LocalQuotaMonitorStats? subscription)
    {
        var menu = SettingsItem(Local("额度设置", "Quota settings", "利用枠の設定"), "quota-settings");
        menu.Items.Add(SettingsItem(Local("本地自动同步", "Automatic local sync", "ローカル自動同期"), "local-sync", ToggleLocalQuotaSync, settings.LocalQuotaSync,
            Local("跟随宿主的新额度更新，不额外联网。没有新数据时保留原值和读取时间。", "Follow new host quota updates without extra network requests. Keep the original reading and time when there is no new data.", "ホストの新しい利用枠と同期し、追加の通信は行いません。新しいデータがなければ前回の値と取得日時を保持します。")));
        var state = !settings.LocalQuotaSync ? "paused" : subscription?.State is "unavailable" or "stopped" ? "unavailable"
            : subscription is { Readings: > 0, LastReceivedAt: not null } ? "received" : "waiting";
        var stateLabel = state switch {
            "paused" => Local("已暂停", "Paused", "一時停止中"),
            "unavailable" => Local("本地数据暂不可用", "Local data unavailable", "ローカルデータを取得できません"),
            "received" => Local("已收到宿主更新", "Host updates received", "ホストの更新を受信済み"),
            _ => Local("等待宿主更新", "Waiting for host updates", "ホストの更新を待機中") };
        var status = SettingsItem(Local("同步状态：", "Sync: ", "同期状態：") + stateLabel, "sync-status-" + state);
        status.IsEnabled = false; menu.Items.Add(status);
        if (settings.LocalQuotaSync && subscription?.LastReceivedAt is { } received)
        {
            var time = SettingsItem(Local("最近同步：", "Last sync: ", "最終同期：") + received.ToLocalTime().ToString("MM-dd HH:mm:ss"), "last-local-sync");
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
        menu.Items.Add(SettingsItem(Local("额度恢复提醒（联网确认）", "Reset reminders (online confirmation)", "リセット通知（オンライン確認）"), "quota-reminders", () => {
            settings = settings with { QuotaResetNotifications = !settings.QuotaResetNotifications };
            if (!settings.QuotaResetNotifications) { resetReminder?.Close(); resetReminder = null; }
            Save();
        }, settings.QuotaResetNotifications, Local("仅在联网读取确认同一账户的额度从0%恢复至100%后提醒。本地同步不会单独触发提醒。", "Notify only after an online reading confirms the same account changed from 0% to 100%. Local sync alone does not trigger a reminder.", "オンライン取得で同一アカウントの0%から100%への回復を確認した場合のみ通知します。ローカル同期のみでは通知しません。")));
        return menu;
    }
}
