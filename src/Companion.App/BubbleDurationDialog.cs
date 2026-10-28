using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace CodexCompanion.App;

internal sealed class BubbleDurationDialog : Window
{
    internal int Seconds { get; private set; }
    internal BubbleDurationDialog(Window owner, int seconds, string language)
    {
        string Text(string zh, string en, string ja) => language switch { "en" => en, "ja" => ja, _ => zh };
        Owner = owner; Seconds = seconds > 0 ? seconds : 7;
        Title = Text("页面持续时间", "Bubble duration", "吹き出しの表示時間");
        Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = Text("额度和问候气泡显示多少秒？（1–86400）", "Show quota and greeting bubbles for how many seconds? (1–86400)", "利用枠・挨拶の表示時間（1～86400 秒）"), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = Seconds.ToString(CultureInfo.CurrentCulture), Margin = new(0, 10, 0, 10), Padding = new(6) }; panel.Children.Add(input);
        var hint = new TextBlock { Text = Text("鼠标悬停会暂停倒计时。需要常驻时请选择“永久”。", "Hovering pauses the countdown. Choose Always open to keep it visible.", "マウスを重ねると一時停止します。常時表示は「常に表示」を選択。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }; panel.Children.Add(hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) }; panel.Children.Add(actions);
        actions.Children.Add(new Button { Content = Text("取消", "Cancel", "キャンセル"), IsCancel = true, MinWidth = 80, Padding = new(10, 5, 10, 5), Margin = new(0, 0, 10, 0) });
        var save = new Button { Content = Text("保存", "Save", "保存"), IsDefault = true, MinWidth = 80, Padding = new(10, 5, 10, 5) }; actions.Children.Add(save);
        save.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value is < 1 or > 86400)
            { hint.Text = Text("请输入 1–86400 之间的整数秒数。", "Enter a whole number from 1 to 86400 seconds.", "1～86400 の整数を入力してください。"); input.Focus(); input.SelectAll(); return; }
            Seconds = value; DialogResult = true;
        };
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
