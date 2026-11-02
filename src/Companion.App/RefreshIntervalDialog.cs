using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace CodexCompanion.App;

internal sealed class RefreshIntervalDialog : Window
{
    internal int Seconds { get; private set; }
    internal RefreshIntervalDialog(Window owner, int seconds, string language)
    {
        string Text(string zh, string en, string ja) => language switch { "en" => en, "ja" => ja, _ => zh };
        Owner = owner; Seconds = seconds; Title = Text("额度查询间隔", "Quota query interval", "利用枠の取得間隔");
        Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = Text("每次查询完成后的等待时间（5–3600 秒）", "Wait after each completed query (5–3600 seconds)", "取得完了後の待機時間（5～3600 秒）"), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = seconds.ToString(CultureInfo.CurrentCulture), Margin = new(0, 10, 0, 8), Padding = new(6) }; panel.Children.Add(input);
        var hint = new TextBlock { Text = Text("仅用于定时查询。每轮优先查询 HTTP 额度，较短间隔会增加流量。", "Used for scheduled queries. Each round prefers HTTP quota; shorter intervals use more data.", "定期取得に適用します。毎回 HTTP を優先し、短い間隔ほど通信量が増えます。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }; panel.Children.Add(hint);
        var button = new Button { Content = Text("保存", "Save", "保存"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80, Padding = new(10, 5, 10, 5), Margin = new(0, 16, 0, 0) };
        button.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value is < 5 or > 3600)
            { hint.Text = Text("请输入 5–3600 之间的整数秒数。", "Enter a whole number from 5 to 3600 seconds.", "5～3600 の整数を入力してください。"); input.Focus(); input.SelectAll(); return; }
            Seconds = value; DialogResult = true;
        };
        panel.Children.Add(button); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
