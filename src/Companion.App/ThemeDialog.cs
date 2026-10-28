using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal sealed class ThemeDialog : Window
{
    internal string TextColor { get; private set; }
    internal string BarColor { get; private set; }
    internal ThemeDialog(Window owner, CompanionSettings settings)
    {
        string Text(string zh, string en, string ja) => settings.Language switch { "en" => en, "ja" => ja, _ => zh };
        Owner = owner; TextColor = settings.QuotaTextColor; BarColor = settings.QuotaBarColor;
        Title = Text("主题", "Theme", "テーマ"); Width = 540; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = Text("额度文字与进度条颜色", "Quota text and progress bar colors", "利用枠の文字とバーの色"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        var presets = new WrapPanel { Margin = new(0, 14, 0, 12) }; panel.Children.Add(presets);
        TextBox Input(string label, string value)
        {
            var row = new DockPanel { Margin = new(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = label, Width = 145, VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { Text = value, MaxLength = 7, Padding = new(6), FontFamily = new("Consolas") };
            row.Children.Add(input); panel.Children.Add(row); return input;
        }
        var textInput = Input(Text("文字颜色", "Text color", "文字の色"), TextColor);
        var barInput = Input(Text("进度条颜色", "Bar color", "バーの色"), BarColor);
        var hint = new TextBlock { Text = Text("输入十六进制颜色，如 #3D2F42。", "Enter a hex color, e.g. #3D2F42.", "#3D2F42 などの16進カラーを入力。"), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(hint);
        var sample = new QuotaPanel();
        panel.Children.Add(new Border { Child = sample, Width = QuotaPanel.BubbleWidth, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(255, 250, 255)), Padding = new(17), Margin = new(0, 16, 0, 0), CornerRadius = new(10) });
        var snapshot = new QuotaSnapshot(QuotaStatus.Live, new(67, 300, DateTimeOffset.Now.AddMinutes(97)), new(85, 10080, DateTimeOffset.Now.AddDays(5)), DateTimeOffset.Now);
        void Preview()
        {
            if (!CompanionSettings.IsThemeColor(textInput.Text) || !CompanionSettings.IsThemeColor(barInput.Text)) return;
            sample.Update(snapshot, settings with { QuotaTextColor = textInput.Text, QuotaBarColor = barInput.Text });
        }
        foreach (var (name, text, bar) in new[] {
            (Text("默认", "Default", "標準"), "#3D2F42", "#00A9D6"),
            (Text("紫藤", "Wisteria", "藤"), "#513A63", "#9871C6"),
            (Text("海蓝", "Ocean", "海"), "#26475E", "#298BC4"),
            (Text("森林", "Forest", "森"), "#305346", "#359A79"),
            (Text("暖橙", "Amber", "琥珀"), "#66432F", "#DB8B46") })
        {
            var button = new Button { Content = name, Padding = new(10, 5, 10, 5), Margin = new(0, 0, 7, 0) };
            button.Click += (_, _) => { textInput.Text = text; barInput.Text = bar; }; presets.Children.Add(button);
        }
        textInput.TextChanged += (_, _) => Preview(); barInput.TextChanged += (_, _) => Preview(); Preview();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) }; panel.Children.Add(actions);
        var cancel = new Button { Content = Text("取消", "Cancel", "キャンセル"), IsCancel = true, MinWidth = 80, Padding = new(10, 5, 10, 5), Margin = new(0, 0, 10, 0) }; actions.Children.Add(cancel);
        var save = new Button { Content = Text("保存", "Save", "保存"), IsDefault = true, MinWidth = 80, Padding = new(10, 5, 10, 5) }; actions.Children.Add(save);
        save.Click += (_, _) =>
        {
            if (!CompanionSettings.IsThemeColor(textInput.Text) || !CompanionSettings.IsThemeColor(barInput.Text))
            { hint.Text = Text("请输入 # 加 6 位十六进制颜色。", "Use # followed by six hex digits.", "# と6桁の16進数を入力してください。"); return; }
            TextColor = textInput.Text.ToUpperInvariant(); BarColor = barInput.Text.ToUpperInvariant(); DialogResult = true;
        };
    }
}
