using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexCompanion.Core;
namespace CodexCompanion.App;

// Two reusable rows, no animated/indeterminate bars or per-refresh controls.
internal sealed class QuotaPanel : StackPanel
{
    internal sealed class Row : StackPanel
    {
        internal readonly TextBlock[] Labels = Enumerable.Range(0, 4).Select(_ => new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }).ToArray();
        internal readonly Grid Track = new() { Height = 4, Margin = new(0, 8, 0, 0), Background = Brushes.Transparent };
        internal readonly Border Fill = new();
        readonly ColumnDefinition filled = new(), empty = new();
        internal Row()
        {
            var line = new Grid { Margin = new(0, 0, 22, 0) };
            for (var i = 0; i < 4; i++)
            {
                line.ColumnDefinitions.Add(new() { Width = i is 1 or 3 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                Labels[i].Margin = new(0, 0, i < 3 ? 12 : 0, 0);
                Grid.SetColumn(Labels[i], i); line.Children.Add(Labels[i]);
            }
            Track.ColumnDefinitions.Add(filled); Track.ColumnDefinitions.Add(empty);
            Track.Children.Add(Fill); Children.Add(line); Children.Add(Track);
        }
        internal void Set(double? remaining, Brush foreground, Brush accent)
        {
            foreach (var label in Labels) label.Foreground = foreground;
            Fill.Background = accent;
            if (!Equals(Track.Tag, accent))
            {
                var color = ((SolidColorBrush)accent).Color;
                var track = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)); track.Freeze();
                Track.Background = track; Track.Tag = accent;
            }
            var percent = remaining is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            filled.Width = new(percent, GridUnitType.Star); empty.Width = new(100 - percent, GridUnitType.Star);
        }
    }
    internal readonly Row FiveHour = new(), Weekly = new() { Margin = new(0, 19, 0, 0) };
    internal readonly TextBlock Footer = new() { FontSize = 11, Margin = new(0, 14, 0, 0) };
    string? textColor, barColor;
    SolidColorBrush foreground = Brushes.Black, accent = Brushes.Transparent;
    internal QuotaPanel() { Children.Add(FiveHour); Children.Add(Weekly); Children.Add(Footer); }
    internal string Update(QuotaSnapshot snapshot, CompanionSettings settings)
    {
        string Text(string zh, string en, string ja) => settings.Language switch { "en" => en, "ja" => ja, _ => zh };
        if (textColor != settings.QuotaTextColor)
        { foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.QuotaTextColor)); foreground.Freeze(); textColor = settings.QuotaTextColor; }
        if (barColor != settings.QuotaBarColor)
        { accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.QuotaBarColor)); accent.Freeze(); barColor = settings.QuotaBarColor; }
        void UpdateRow(Row row, string name, QuotaWindow? value)
        {
            row.Labels[0].Text = name;
            row.Labels[1].Text = value?.ResetsAt?.ToLocalTime().ToString("HH:mm") ?? "—";
            row.Labels[1].ToolTip = value?.ResetsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var left = value?.ResetsAt - DateTimeOffset.Now;
            var duration = left is { } time && time > TimeSpan.Zero
                ? time.TotalDays >= 1 ? $"{(int)time.TotalDays}d{time.Hours}h" : $"{(int)time.TotalHours}h{time.Minutes}m"
                : null;
            row.Labels[2].Text = duration is not null ? duration + Text(" 后重置", " to reset", " 後にリセット")
                : left.HasValue ? Text("等待重置", "Reset pending", "更新待ち") : Text("重置时间未知", "Reset unknown", "リセット不明");
            row.Labels[3].Text = value is null ? Text("未知", "Unavailable", "不明")
                : value.RemainingPercent.ToString("0.#", CultureInfo.CurrentCulture) + Text("% 剩余", "% left", "% 残り");
            row.Set(value?.RemainingPercent, foreground, accent);
        }
        UpdateRow(FiveHour, Text("5小时限额", "5-hour limit", "5時間の利用枠"), snapshot.FiveHour);
        UpdateRow(Weekly, Text("周限额", "Weekly limit", "週間の利用枠"), snapshot.Weekly);
        Footer.Text = snapshot.UpdatedAt is { } updated ? Text("读取于 ", "Read at ", "取得日時 ") + updated.ToLocalTime().ToString("MM-dd HH:mm:ss")
            : snapshot.Status == QuotaStatus.NotLoggedIn ? Text("请先在 Codex 登录 ChatGPT 账户。", "Sign in to ChatGPT in Codex.", "Codex で ChatGPT にログインしてください。") : Text("读取于 —", "Read at —", "取得日時 —");
        Footer.Foreground = foreground;
        return string.Join("|", FiveHour.Labels.Concat(Weekly.Labels).Select(label => label.Text)) + Footer.Text;
    }
}
