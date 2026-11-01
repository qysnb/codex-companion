using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace CodexCompanion.App;

internal sealed class LocalSyncIntervalDialog : Window
{
    internal int Milliseconds { get; private set; }
    internal static bool TryParseMilliseconds(string input, CultureInfo culture, out int milliseconds)
    {
        milliseconds = 0;
        if (!decimal.TryParse(input.Trim(), NumberStyles.AllowDecimalPoint, culture, out var seconds) ||
            seconds < 0 || seconds > 3600 || seconds > 0 && seconds < .1m) return false;
        var value = seconds * 1000;
        if (value != decimal.Truncate(value)) return false;
        milliseconds = (int)value; return true;
    }
    internal LocalSyncIntervalDialog(Window owner, int milliseconds, string language)
    {
        string Text(string zh, string en, string ja) => language switch { "en" => en, "ja" => ja, _ => zh };
        Owner = owner; Milliseconds = milliseconds; Title = Text("本地同步间隔", "Local sync interval", "ローカル同期の間隔");
        Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = Text("输入秒数：0 表示即时，或 0.1–3600 秒。", "Seconds: 0 for immediate, or 0.1–3600 seconds.", "秒数を入力：0は即時、または0.1～3600秒。"), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = (milliseconds / 1000m).ToString("0.###", CultureInfo.CurrentCulture), Margin = new(0, 10, 0, 8), Padding = new(6) }; panel.Children.Add(input);
        var hint = new TextBlock { Text = Text("固定间隔内合并更新，读取时只显示最新额度。仅影响本地读取，不发起联网；支持最多三位小数。", "Updates within an interval are combined into the latest reading. Local reads only, no network requests. Up to three decimal places.", "間隔内の更新は最新の利用枠にまとめます。ローカル読取のみで通信しません。小数点以下3桁まで指定できます。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }; panel.Children.Add(hint);
        var save = new Button { Content = Text("保存", "Save", "保存"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80, Padding = new(10, 5, 10, 5), Margin = new(0, 16, 0, 0) };
        save.Click += (_, _) => {
            if (!TryParseMilliseconds(input.Text, CultureInfo.CurrentCulture, out var value))
            { hint.Text = Text("请输入 0 或 0.1–3600 秒，最多三位小数。", "Enter 0 or 0.1–3600 seconds, with up to three decimal places.", "0または0.1～3600秒を、小数点以下3桁までで入力してください。"); input.Focus(); input.SelectAll(); return; }
            Milliseconds = value; DialogResult = true;
        };
        panel.Children.Add(save); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
