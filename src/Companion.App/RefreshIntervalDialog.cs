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
        Owner = owner; Seconds = seconds; Title = Text("额度刷新间隔", "Quota refresh interval", "利用枠の更新間隔");
        Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = Text("每隔多少秒在后台刷新？（5–3600）", "Refresh in the background every how many seconds? (5–3600)", "バックグラウンド更新の間隔（5～3600 秒）"), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = seconds.ToString(CultureInfo.CurrentCulture), Margin = new(0, 10, 0, 8), Padding = new(6) }; panel.Children.Add(input);
        var hint = new TextBlock { Text = Text("间隔越短，后台请求和资源占用越多。", "Shorter intervals use more background requests and resources.", "短い間隔ほど通信回数とリソース使用量が増えます。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }; panel.Children.Add(hint);
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
