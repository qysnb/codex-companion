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
            foreach (var interval in new[] { 15, 900 })
            {
                settings = saved with { Language = language, QuotaNetworkMode = mode, LocalQuotaSync = sync, RefreshIntervalSeconds = interval };
                var before = settings; var root = BuildMenu(); var quotaMenu = Find(root, "quota-settings");
                if (root.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "local-sync") || Equals(item.Tag, "network-mode")))
                    throw new InvalidOperationException("Quota controls leaked into the main menu.");
                var local = Find(quotaMenu, "local-sync");
                if (!local.IsCheckable || local.IsChecked != sync) throw new InvalidOperationException("Local sync preference is not reflected.");
                var network = Find(quotaMenu, "network-mode");
                if (network.Items.OfType<MenuItem>().Count(item => item.IsChecked) != 1 || !Find(network, "network-" + mode).IsChecked)
                    throw new InvalidOperationException("Network supplement modes must have exactly one selected value.");
                var intervalMenus = quotaMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "online-interval")).ToArray();
                if (intervalMenus.Length != (mode == "automatic" ? 1 : 0)) throw new InvalidOperationException("Online interval must only be shown for scheduled supplements.");
                if (mode == "automatic")
                {
                    var expected = interval == 15 ? "interval-custom" : "interval-900";
                    if (!Find(intervalMenus[0], expected).IsChecked) throw new InvalidOperationException("Existing custom or preset interval lost its selection.");
                }
                if (Find(quotaMenu, "refresh-online").ToolTip is null || Find(quotaMenu, "quota-reminders").ToolTip is null)
                    throw new InvalidOperationException("Online actions must explain their network/account scope.");
                if (settings != before) throw new InvalidOperationException("Opening settings changed a stored preference.");
                scenarios++;
            }
            foreach (var language in CompanionSettings.Languages)
            {
                foreach (var state in new[] { "paused", "waiting", "received", "unavailable" })
                {
                    settings = saved with { Language = language, LocalQuotaSync = state != "paused" };
                    var stats = new LocalQuotaMonitorStats(state == "unavailable" ? "unavailable" : "listening", 1, 16384, 100, state == "received" ? 1 : 0, state == "received" ? DateTimeOffset.Now : null);
                    var quotaMenu = BuildQuotaSettingsMenu(stats);
                    if (Find(quotaMenu, "sync-status-" + state).IsEnabled) throw new InvalidOperationException("Sync status must be informational.");
                    if (quotaMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "last-local-sync")) != (state == "received"))
                        throw new InvalidOperationException("Last sync time must only be shown when a local event was received.");
                }
            }
            if (previewPath is not null)
            {
                settings = saved with { Language = "zh-CN", QuotaNetworkMode = "local", LocalQuotaSync = true };
                var example = new LocalQuotaMonitorStats("listening", 2, 24576, 100, 1, DateTimeOffset.Now);
                static ContextMenu Extract(MenuItem item)
                {
                    var children = item.Items.Cast<object>().ToArray(); item.Items.Clear();
                    var menu = new ContextMenu(); foreach (var child in children) menu.Items.Add(child); return menu;
                }
                var roots = new[] { BuildMenu(), Extract(BuildQuotaSettingsMenu(example)), Extract(Find(BuildQuotaSettingsMenu(example), "network-mode")) };
                var headings = new[] { "主菜单", "额度设置", "联网补充方式" };
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
            return new { menuScenarios = scenarios, syncStateScenarios = 12, settingsPreserved = true, previewPath };
        }
        finally { settings = saved; validationMode = wasValidation; ContextMenu = null; }
    }
}
