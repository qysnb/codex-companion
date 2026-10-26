using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexCompanion.Core;

namespace CodexCompanion.App;

/// <summary>A single short-lived, non-activating reminder. No tray host or resident timer.</summary>
internal sealed class QuotaResetReminder : Window
{
    readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(8) };
    internal QuotaResetReminder(string language, QuotaResetNotice notice, PixelRect work, double scale)
    {
        string L(string zh, string en, string ja) => language switch { "en" => en, "ja" => ja, _ => zh };
        Title = "Codex Companion"; ShowActivated = false; ShowInTaskbar = false; Topmost = true; Focusable = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        Width = Math.Max(180, Math.Min(360, work.Width / scale - 32)); SizeToContent = SizeToContent.Height;
        var stack = new StackPanel();
        var header = new DockPanel();
        var close = new Button { Content = "×", Width = 24, Height = 24, FontSize = 18, Background = Brushes.Transparent, BorderThickness = new(0), Focusable = false,
            ToolTip = L("关闭", "Close", "閉じる") };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = L("Codex 额度已恢复", "Codex quota restored", "Codex 利用枠が回復しました"), FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(116, 77, 116)), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(header);
        var windows = notice.FiveHour && notice.Weekly ? L("五小时限额和周限额", "Five-hour and weekly quotas", "5 時間枠と週間枠")
            : notice.FiveHour ? L("五小时限额", "Five-hour quota", "5 時間枠") : L("周限额", "Weekly quota", "週間枠");
        stack.Children.Add(new TextBlock { Text = windows + L("已从 0% 重置为 100%。", " reset from 0% to 100%.", "が 0% から 100% にリセットされました。"),
            Margin = new(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(61, 47, 66)) });
        Content = new Border { Child = stack, Padding = new(18), CornerRadius = new(16), BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 207, 230)), Background = new SolidColorBrush(Color.FromRgb(255, 250, 255)) };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeWindows.SetWindowLong(hwnd, NativeWindows.GwlExStyle, NativeWindows.GetWindowLong(hwnd, NativeWindows.GwlExStyle) | NativeWindows.WsExToolwindow | NativeWindows.WsExNoactivate);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivate);
        };
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var width = (int)Math.Ceiling(ActualWidth * scale); var height = (int)Math.Ceiling(ActualHeight * scale);
            NativeWindows.SetWindowPos(hwnd, new nint(-1), (int)(work.X + work.Width - width - 16 * scale), (int)(work.Y + work.Height - height - 16 * scale), width, height, 0x10);
            expiry.Start();
        };
        expiry.Tick += (_, _) => Close();
        Closed += (_, _) => expiry.Stop();
    }
    static nint NoActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return 3; } return 0; }
}
