using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal sealed partial class CompanionWindow
{
    internal object VerifySettingsMenus(string? previewPath)
    {
        var saved = settings; var wasValidation = validationMode; validationMode = true;
        var scenarios = 0;
        static MenuItem Find(ItemsControl parent, string id) => parent.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, id));
        try
        {
            foreach (var language in CompanionSettings.Languages)
            foreach (var mode in new[] { "local", "on-demand", "automatic" })
            foreach (var sync in new[] { false, true })
            foreach (var interval in new[] { 15, 30, 900 })
            foreach (var localInterval in new[] { 0, 1250 })
            {
                settings = saved with { Language = language, QuotaNetworkMode = mode, LocalQuotaSync = sync, RefreshIntervalSeconds = interval, LocalQuotaSyncIntervalMs = localInterval };
                var before = settings; var root = BuildMenu(); var quotaMenu = Find(root, "quota-settings");
                if (root.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "local-sync") || Equals(item.Tag, "network-mode")))
                    throw new InvalidOperationException("Quota controls leaked into the main menu.");
                var localOptions = Find(quotaMenu, "local-options");
                if (quotaMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "local-sync") || Equals(item.Tag, "local-interval") || Equals(item.Tag, "last-local-sync")))
                    throw new InvalidOperationException("Local event details must stay in their auxiliary submenu.");
                var primary = quotaMenu.Items.OfType<MenuItem>().ToArray();
                if (!Equals(primary[0].Tag, "network-mode") || Array.IndexOf(primary, Find(quotaMenu, "refresh-online")) >= Array.IndexOf(primary, localOptions))
                    throw new InvalidOperationException("Primary quota query actions must precede local auxiliary controls.");
                if (Find(quotaMenu, "quota-source").IsEnabled || Find(quotaMenu, "quota-last-read").IsEnabled)
                    throw new InvalidOperationException("Quota provenance and successful read time must be informational.");
                var local = Find(localOptions, "local-sync");
                if (!local.IsCheckable || local.IsChecked != sync) throw new InvalidOperationException("Local sync preference is not reflected.");
                var localIntervals = localOptions.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "local-interval")).ToArray();
                if (localIntervals.Length != (sync ? 1 : 0)) throw new InvalidOperationException("Local interval must only be shown when local sync is enabled.");
                if (sync && !Find(localIntervals[0], localInterval == 0 ? "local-interval-0" : "local-interval-custom").IsChecked)
                    throw new InvalidOperationException("Immediate/custom local interval selection was lost.");
                var network = Find(quotaMenu, "network-mode");
                if (network.Items.OfType<MenuItem>().Count(item => item.IsChecked) != 1 || !Find(network, "network-" + mode).IsChecked)
                    throw new InvalidOperationException("Network supplement modes must have exactly one selected value.");
                var intervalMenus = quotaMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "online-interval")).ToArray();
                if (intervalMenus.Length != (mode == "automatic" ? 1 : 0)) throw new InvalidOperationException("Online interval must only be shown for scheduled supplements.");
                if (mode == "automatic")
                {
                    var expected = interval == 15 ? "interval-custom" : "interval-" + interval;
                    if (!Find(intervalMenus[0], expected).IsChecked) throw new InvalidOperationException("Existing custom or preset interval lost its selection.");
                }
                if (Find(quotaMenu, "refresh-online").ToolTip is null)
                    throw new InvalidOperationException("Online refresh must explain its network scope.");
                var reminders = Find(quotaMenu, "quota-reminders");
                var reminderLabel = language switch { "zh-CN" => "额度恢复提醒", "ja" => "利用枠の回復通知", _ => "Quota recovery reminders" };
                if (!Equals(reminders.Header, reminderLabel) || reminders.ToolTip is not null ||
                    !reminders.IsCheckable || reminders.IsChecked != settings.QuotaResetNotifications)
                    throw new InvalidOperationException("Quota recovery reminders must retain their preference without an online-only label or tooltip.");
                if (settings != before) throw new InvalidOperationException("Opening settings changed a stored preference.");
                scenarios++;
            }
            foreach (var (text, culture, expected) in new[] {
                ("0", "en-US", 0), ("0.1", "en-US", 100), ("0.5", "en-US", 500), ("1.234", "en-US", 1234),
                ("3600", "en-US", 3600000), ("0,5", "de-DE", 500),
                ("0.01", "en-US", -1), ("-1", "en-US", -1), ("3600.001", "en-US", -1), ("1.2345", "en-US", -1), ("NaN", "en-US", -1), ("", "en-US", -1) })
            {
                var accepted = LocalSyncIntervalDialog.TryParseMilliseconds(text, CultureInfo.GetCultureInfo(culture), out var value);
                if (accepted != (expected >= 0) || accepted && value != expected) throw new InvalidOperationException("Custom local interval validation failed.");
            }
            var oldMonitor = localQuotaMonitor;
            var probe = new LocalQuotaMonitor(Path.Combine(Path.GetTempPath(), "companion-menu-cadence-" + Guid.NewGuid()));
            try
            {
                localQuotaMonitor = probe; var generation = probe.Generation; var online = settings.QuotaNetworkMode; var onlineInterval = settings.RefreshIntervalSeconds;
                SetLocalSyncInterval(5000);
                if (probe.IntervalMilliseconds != 5000 || settings.LocalQuotaSyncIntervalMs != 5000 || probe.Generation != generation)
                    throw new InvalidOperationException("The menu must apply local cadence to the existing subscription.");
                SetLocalSyncInterval(0);
                if (probe.IntervalMilliseconds != 0 || settings.QuotaNetworkMode != online || settings.RefreshIntervalSeconds != onlineInterval)
                    throw new InvalidOperationException("Local cadence must not alter network preferences.");
            }
            finally { localQuotaMonitor = oldMonitor; probe.DisposeAsync().GetAwaiter().GetResult(); }
            foreach (var language in CompanionSettings.Languages)
            {
                var expiredPanel = new QuotaPanel();
                var observed = DateTimeOffset.Now.AddHours(-8);
                expiredPanel.Update(new(QuotaStatus.Live, new(8, 300, DateTimeOffset.Now.AddHours(-7)),
                    new(70, 10080, DateTimeOffset.Now.AddDays(6)), observed), saved with { Language = language }, DateTimeOffset.Now);
                var pendingLabel = language switch { "en" => "Pending", "ja" => "同期待ち", _ => "待同步" };
                if (expiredPanel.FiveHour.Labels[3].Text != pendingLabel || expiredPanel.Weekly.Labels[3].Text != (language switch { "en" => "70% left", "ja" => "70%残り", _ => "70%剩余" }))
                    throw new InvalidOperationException("An expired quota window must not display its old percentage as current or hide an unexpired window.");
            }
            foreach (var language in CompanionSettings.Languages)
            {
                var dataTime = DateTimeOffset.Now.AddHours(-1); var readTime = DateTimeOffset.Now;
                var snapshot = new QuotaSnapshot(QuotaStatus.Live, new(44, 300, readTime.AddHours(2)), new(75, 10080, readTime.AddDays(4)), dataTime);
                var panel = new QuotaPanel(); panel.Update(snapshot, saved with { Language = language }, readTime);
                if (!panel.Footer.Text.EndsWith(readTime.ToLocalTime().ToString("MM-dd HH:mm:ss"), StringComparison.Ordinal) ||
                    panel.Footer.Text.Contains('\n') || panel.Footer.ToolTip?.ToString()?.Contains(dataTime.ToLocalTime().ToString("MM-dd HH:mm:ss"), StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Read footer must show successful read time and keep original data time in its tooltip.");
            }
            foreach (var language in CompanionSettings.Languages)
            {
                foreach (var state in new[] { "paused", "waiting", "received", "unavailable" })
                {
                    settings = saved with { Language = language, LocalQuotaSync = state != "paused" };
                    var stats = new LocalQuotaMonitorStats(state == "unavailable" ? "unavailable" : "listening", 1, 16384, 100, state == "received" ? 1 : 0, state == "received" ? DateTimeOffset.Now : null);
                    var quotaMenu = BuildQuotaSettingsMenu(stats);
                    var localOptions = Find(quotaMenu, "local-options");
                    if (Find(localOptions, "sync-status-" + state).IsEnabled) throw new InvalidOperationException("Sync status must be informational.");
                    if (localOptions.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "last-local-sync")) != (state == "received"))
                        throw new InvalidOperationException("Last sync time must only be shown when a local event was received.");
                }
            }
            var provenanceScenarios = 0;
            foreach (var language in CompanionSettings.Languages)
            foreach (var source in new[] { "none", "http", "app-server", "local-session" })
            {
                settings = saved with { Language = language };
                var sample = new MenuItem(); var stamp = source == "none" ? (DateTimeOffset?)null : DateTimeOffset.Parse("2026-11-02T04:48:53Z");
                AddQuotaReadStatus(sample, source, stamp);
                var expectedSource = source switch {
                    "http" => language switch { "en" => "Online query", "ja" => "オンライン取得", _ => "在线查询" },
                    "app-server" => language switch { "en" => "Compatibility query", "ja" => "互換取得", _ => "兼容查询" },
                    "local-session" => language switch { "en" => "Local events", "ja" => "ローカルイベント", _ => "本地事件" },
                    _ => language switch { "en" => "No data yet", "ja" => "データ未取得", _ => "暂无数据" } };
                if (!Find(sample, "quota-source").Header.ToString()!.EndsWith(expectedSource, StringComparison.Ordinal) ||
                    !Find(sample, "quota-last-read").Header.ToString()!.EndsWith(stamp?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—", StringComparison.Ordinal))
                    throw new InvalidOperationException("Displayed quota source/read time must describe the selected quota, including HTTP, recovery, local and empty cases.");
                provenanceScenarios++;
            }
            if (previewPath is not null)
            {
                settings = saved with { Language = "zh-CN", QuotaNetworkMode = "automatic", RefreshIntervalSeconds = 30, LocalQuotaSync = true, LocalQuotaSyncIntervalMs = 0 };
                var example = new LocalQuotaMonitorStats("listening", 2, 24576, 100, 1, DateTimeOffset.Now);
                static ContextMenu Extract(MenuItem item)
                {
                    var children = item.Items.Cast<object>().ToArray(); item.Items.Clear();
                    var menu = new ContextMenu(); foreach (var child in children) menu.Items.Add(child); return menu;
                }
                var roots = new[] { BuildMenu(), Extract(BuildQuotaSettingsMenu(example)), Extract(Find(BuildQuotaSettingsMenu(example), "local-options")) };
                var headings = new[] { "主菜单", "额度设置", "本地事件辅助" };
                var sizes = new List<Size>();
                foreach (var menu in roots)
                {
                    menu.Visibility = Visibility.Visible; menu.ApplyTemplate();
                    menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout(); sizes.Add(menu.DesiredSize);
                }
                var width = sizes.Sum(size => size.Width) + 80; var height = sizes.Max(size => size.Height) + 64;
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 243, 247)), null, new Rect(0, 0, width, height));
                    var x = 20d;
                    for (var i = 0; i < roots.Length; i++)
                    {
                        drawing.DrawText(new FormattedText(headings[i], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.FromRgb(61, 47, 66)), 1), new Point(x, 12));
                        drawing.DrawRectangle(new VisualBrush(roots[i]) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(x, 40, sizes[i].Width, sizes[i].Height));
                        x += sizes[i].Width + 20;
                    }
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.GetFullPath(previewPath)); encoder.Save(output);
            }
            return new { menuScenarios = scenarios, provenanceScenarios, syncStateScenarios = 12, customInputScenarios = 12, readTimeScenarios = 3, liveLocalIntervalBinding = true, settingsPreserved = true, previewPath };
        }
        finally { settings = saved; validationMode = wasValidation; ContextMenu = null; }
    }
}
