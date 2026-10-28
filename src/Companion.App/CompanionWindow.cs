using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexCompanion.Core;
using Microsoft.Win32;
namespace CodexCompanion.App;

internal sealed partial class CompanionWindow : Window
{
    readonly SettingsStore store = new();
    CompanionSettings settings;
    readonly CompanionController controller = new();
    readonly QuotaService quota;
    readonly Dictionary<string, ShuffleBag> greetings = new();
    readonly Image character = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new(1, 1) };
    readonly Image departing = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new(1, 1), IsHitTestVisible = false };
    readonly TextBlock preview = new() { FontSize = 10, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(210, 60, 49, 70)), Text = "REFERENCE PREVIEW", Padding = new(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    readonly Border bubble;
    readonly Popup bubblePopup;
    readonly TextBlock bubbleText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Foreground = new SolidColorBrush(Color.FromRgb(61, 47, 66)) };
    readonly TextBlock bubbleTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(116, 77, 116)), Margin = new(0, 0, 0, 7) };
    readonly QuotaPanel quotaPanel = new() { Visibility = Visibility.Collapsed };
    readonly BubbleLifetime lifetime = new();
    readonly DispatcherTimer frame = new() { Interval = ResourcePolicy.TrackingInterval(false) };
    readonly DispatcherTimer motionFrame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly ScaleTransform pokeScale = new(1, 1);
    readonly TranslateTransform pokeOffset = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    CommandPipe? pipe;
    QuotaResetReminder? resetReminder;
    nint handle;
    bool manuallyHidden, stopping, dragging, moved, artPreview, settingsDialogOpen;
    LongPressDrag? dragGesture;
    Point pressScreen;
    double pressX, pressY;
    TimeSpan? pokeStarted;
    bool pokeReduced;
    PokePose pokeInitial = PokePose.Rest;
    double decodedScale = 1;
    double displayHeight;
    long lastQuotaSecond = -1;
    DateTimeOffset? hiddenSince;
    PixelRect? lastPlacement;
    BitmapSource? idle;
    BitmapSource? pixelsSource;
    byte[]? pixels;
    PixelRect? lastBubblePlacement;
    (string Title, string Text)? measuredBubble;
    double lastQuotaDisplayMs;
    bool validationMode;
    (nint Handle, PixelRect Rect, PixelRect Work, double Scale)? target;
    TimeSpan lastTick;
    public CompanionWindow(QuotaService? quotaService = null)
    {
        quota = quotaService ?? new();
        settings = store.Load(); controller.SetCharacter(settings.Character); controller.SetLanguage(settings.Language);
        displayHeight = settings.Height;
        quota.RefreshInterval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        Title = "Codex Companion"; Width = displayHeight + 12; Height = displayHeight + 12;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        var canvas = new Grid(); Content = canvas;
        var motion = new TransformGroup(); motion.Children.Add(pokeScale); motion.Children.Add(pokeOffset); character.RenderTransform = departing.RenderTransform = motion;
        var stack = new StackPanel();
        var close = new Button { Content = "×", FontSize = 18, Width = 24, Height = 24, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0), Focusable = false, ToolTip = "Close / 关闭 / 閉じる" };
        close.Click += (_, _) => CloseBubble();
        bubbleTitle.MinHeight = 24; bubbleTitle.Margin = new(0, 0, 24, 7);
        stack.Children.Add(bubbleTitle); stack.Children.Add(bubbleText); stack.Children.Add(quotaPanel);
        var bubbleContent = new Grid(); bubbleContent.Children.Add(stack);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Margin = new(0, -6, -9, 0);
        bubbleContent.Children.Add(close);
        bubble = new Border { Child = bubbleContent, Background = new SolidColorBrush(Color.FromArgb(248, 255, 250, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(229, 207, 230)), BorderThickness = new(1), CornerRadius = new(17), Padding = new(17, 14, 17, 14), Width = 320, MaxHeight = 215, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        canvas.Children.Add(departing); canvas.Children.Add(character); canvas.Children.Add(preview);
        bubblePopup = new Popup { Child = bubble, PlacementTarget = character, Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = true, Focusable = false, PopupAnimation = PopupAnimation.None };
        bubblePopup.Opened += (_, _) =>
        {
            lastBubblePlacement = null;
            if (PresentationSource.FromVisual(bubble) is HwndSource source)
                NativeWindows.SetWindowLong(source.Handle, NativeWindows.GwlExStyle, NativeWindows.GetWindowLong(source.Handle, NativeWindows.GwlExStyle) | NativeWindows.WsExNoactivate);
            PositionBubble();
        };
        bubblePopup.Closed += (_, _) => lastBubblePlacement = null;
        bubble.SizeChanged += (_, _) => Dispatcher.BeginInvoke(PositionBubble, DispatcherPriority.Render);
        MouseLeftButtonDown += Press; MouseMove += Move; MouseLeftButtonUp += Release;
        LostMouseCapture += (_, _) => dragging = false;
        MouseRightButtonUp += (_, e) => { BuildMenu().IsOpen = true; e.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            NativeWindows.SetWindowLong(handle, NativeWindows.GwlExStyle, NativeWindows.GetWindowLong(handle, NativeWindows.GwlExStyle) | NativeWindows.WsExToolwindow | NativeWindows.WsExNoactivate);
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        };
        quota.Updated += _ => Dispatcher.BeginInvoke(() => { if (controller.Bubble == BubbleKind.Quota && lifetime.IsOpen) RenderQuota(); });
        quota.ResetDetected += OnQuotaReset;
        frame.Tick += Tick;
        motionFrame.Tick += (_, _) => AnimateFrame();
        LoadDialogue(); LoadArt();
        // Prepare the current locale's text formatting before the first click.
        bubbleTitle.Text = CharacterName(settings.Character);
        bubbleText.Text = Local("5 小时 · 每周 · 0123456789% 剩余", "5 hours · Weekly · 0123456789% remaining", "5 時間 · 週間 · 0123456789% 残り");
        bubble.Visibility = Visibility.Visible; bubble.Measure(new Size(320, 215));
        bubble.Visibility = Visibility.Collapsed; bubbleText.Text = "";
    }
    internal Task StartAsync()
    {
        new WindowInteropHelper(this).EnsureHandle();
        pipe = new CommandPipe(request => Dispatcher.InvokeAsync(() => ExecuteAsync(request)).Task.Unwrap());
        frame.Start(); _ = quota.StartAsync(); Tick(null, EventArgs.Empty); return Task.CompletedTask;
    }
    internal async Task StopAsync()
    {
        if (stopping) return; stopping = true; frame.Stop(); ResetPoke(); CompleteExpression(); bubblePopup.IsOpen = false;
        quota.ResetDetected -= OnQuotaReset; resetReminder?.Close(); resetReminder = null;
        if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
        await quota.DisposeAsync().ConfigureAwait(false);
    }
    void Save() { try { store.Save(settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowMessage(Local("无法保存设置", "Settings could not be saved", "設定を保存できません"), e.GetType().Name, 7); } }
    void LoadDialogue()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "dialogue.json")));
            foreach (var person in json.RootElement.EnumerateObject()) foreach (var lang in person.Value.EnumerateObject())
                greetings[person.Name + "/" + lang.Name] = new ShuffleBag(lang.Value.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException) { }
    }
    BitmapSource? LoadPng(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelHeight = ResourcePolicy.DecodeHeight(displayHeight, decodedScale);
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    void LoadArt()
    {
        var nextHeight = (double)settings.Height;
        if (target is { } t)
        { decodedScale = t.Scale; nextHeight = ResourcePolicy.FitDisplayHeight(settings.Height, t.Work, t.Scale); }
        if (Math.Abs(displayHeight - nextHeight) > .01) ResetPoke();
        displayHeight = nextHeight;
        ReleaseArt();
        var root = Path.Combine(AppContext.BaseDirectory, "assets", "characters", settings.Character);
        var stripPath = Path.Combine(root, "registered.png");
        if (File.Exists(stripPath))
        {
            var sheet = CharacterArt.DecodeStrip(stripPath, ResourcePolicy.DecodeHeight(displayHeight, decodedScale));
            artFrames = CharacterArt.SharedFrames(sheet);
            idle = artFrames[0].Image;
        }
        else
        { idle = LoadPng(Path.Combine(root, "idle.png")); }
        artPreview = idle is null;
        idle ??= LoadPng(Path.Combine(AppContext.BaseDirectory, "assets", "references", settings.Character + ".png"));
        if (artFrames is null && idle is not null)
        { var fallback = CharacterFrame.FromImage(idle); artFrames = new[] { fallback, fallback, fallback }; }
        preview.Visibility = artPreview ? Visibility.Visible : Visibility.Collapsed;
        character.Height = departing.Height = displayHeight; character.Width = departing.Width = displayHeight;
        UpdateWindowSize();
        SyncExpression(true);
    }
    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x84)
        {
            var value = lParam.ToInt64(); var screen = new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
            var point = PointFromScreen(screen);
            var imagePoint = TranslatePoint(point, character); var width = character.ActualWidth; var height = character.ActualHeight;
            var hit = false;
            if (pixelsSource is not null && pixels is not null && width > 0 && height > 0)
            {
                var ratio = Math.Min(width / pixelsSource.PixelWidth, height / pixelsSource.PixelHeight);
                var x = (int)((imagePoint.X - (width - pixelsSource.PixelWidth * ratio) / 2) / ratio);
                var y = (int)((imagePoint.Y - (height - pixelsSource.PixelHeight * ratio) / 2) / ratio);
                if (x >= 0 && y >= 0 && x < pixelsSource.PixelWidth && y < pixelsSource.PixelHeight)
                {
                    var index = y * pixelsSource.PixelWidth + x;
                    var previous = outgoingFrame?.Alpha[index] ?? 0;
                    hit = previous + (255 - previous) * pixels[index] * character.Opacity / 255 > 12;
                }
            }
            handled = true; return hit ? 1 : -1; // HTCLIENT / HTTRANSPARENT
        }
        return 0;
    }
    bool IsInside(FrameworkElement element, Point point) { var local = TranslatePoint(point, element); return local.X >= 0 && local.Y >= 0 && local.X < element.ActualWidth && local.Y < element.ActualHeight; }
    void Tick(object? sender, EventArgs e)
    {
        if (validationMode) return;
        var now = elapsed.Elapsed; var delta = now - lastTick; lastTick = now;
        if (!dragging && !settingsDialogOpen && ContextMenu?.IsOpen != true) target = NativeWindows.ForegroundCodex();
        var visible = !manuallyHidden && target is not null;
        frame.Interval = ResourcePolicy.TrackingInterval(visible);
        quota.IsVisible = visible;
        if (!visible)
        {
            ResetPoke(); CompleteExpression();
            bubblePopup.IsOpen = false;
            if (IsVisible) Hide(); hiddenSince ??= DateTimeOffset.Now;
            if (DateTimeOffset.Now - hiddenSince > TimeSpan.FromSeconds(45) && idle is not null)
            { ReleaseArt(); }
            return;
        }
        hiddenSince = null;
        if (target is { } dpiTarget && (Math.Abs(decodedScale - dpiTarget.Scale) > .01 || Math.Abs(displayHeight - ResourcePolicy.FitDisplayHeight(settings.Height, dpiTarget.Work, dpiTarget.Scale)) > .01)) LoadArt();
        if (idle is null) LoadArt();
        if (!IsVisible) Show();
        if (!dragging && target is { } t)
        {
            var rect = CompanionPlacement.Place(t.Rect, t.Work, Width, Height, t.Scale, settings.OffsetX, settings.OffsetY);
            if (rect != lastPlacement)
            { NativeWindows.SetWindowPos(handle, new nint(-1), (int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height), 0x10 | 0x40); lastPlacement = rect; }
        }
        lifetime.Advance(delta, IsMouseOver || bubble.IsMouseOver || ContextMenu?.IsOpen == true);
        if (!lifetime.IsOpen && bubble.Visibility == Visibility.Visible) CloseBubble();
        if (lifetime.IsOpen && bubble.Visibility == Visibility.Visible)
        { if (!bubblePopup.IsOpen) bubblePopup.IsOpen = true; PositionBubble(); }
        if (bubble.Visibility == Visibility.Visible && controller.Bubble == BubbleKind.Quota && (long)now.TotalSeconds != lastQuotaSecond)
        { lastQuotaSecond = (long)now.TotalSeconds; RenderQuota(); }
    }
    void UpdateWindowSize()
    {
        var size = displayHeight + 12;
        if (Width != size || Height != size) { Width = Height = size; lastPlacement = null; }
        PositionBubble();
    }
    void PositionBubble()
    {
        if (!bubblePopup.IsOpen || !IsVisible || target is not { } t || PresentationSource.FromVisual(bubble) is not HwndSource popupSource) return;
        MeasureBubble();
        var popupDpi = VisualTreeHelper.GetDpi(bubble);
        var width = (int)Math.Ceiling(bubble.Width * popupDpi.DpiScaleX);
        var height = (int)Math.Ceiling(bubble.Height * popupDpi.DpiScaleY);
        // Anchor to the character's layout box, excluding the transient poke
        // transform. Neither a shorter line nor a squash can move its anchor.
        var top = PointToScreen(new Point(ActualWidth - character.ActualWidth, ActualHeight - character.ActualHeight));
        var rect = BubblePlacement.Place(new(top.X, top.Y, character.ActualWidth * t.Scale, character.ActualHeight * t.Scale), t.Work, width, height, t.Scale);
        NativeWindows.GetWindowRect(popupSource.Handle, out var native);
        if (rect == lastBubblePlacement && native.Right - native.Left == width && native.Bottom - native.Top == height && Math.Abs(native.Left - rect.X) < 1 && Math.Abs(native.Top - rect.Y) < 1) return;
        NativeWindows.SetWindowPos(popupSource.Handle, 0, (int)Math.Round(rect.X), (int)Math.Round(rect.Y), width, height, 0x10 | 0x04);
        lastBubblePlacement = rect;
    }
    void MeasureBubble()
    {
        var isQuota = controller.Bubble == BubbleKind.Quota;
        bubbleTitle.Visibility = bubbleText.Visibility = isQuota ? Visibility.Collapsed : Visibility.Visible;
        quotaPanel.Visibility = isQuota ? Visibility.Visible : Visibility.Collapsed;
        var width = isQuota ? settings.Language == "en" ? 500 : 450 : 320;
        if (bubble.Width != width) { bubble.Width = width; measuredBubble = null; }
        var content = (bubbleTitle.Text, bubbleText.Text);
        if (content == measuredBubble) return;
        // Discard the previous popup's height, including after auto-dismissal.
        bubble.Height = double.NaN;
        bubble.InvalidateMeasure(); bubble.Measure(new Size(bubble.Width, double.PositiveInfinity));
        bubble.Height = Math.Ceiling(bubble.DesiredSize.Height);
        measuredBubble = content;
        bubble.UpdateLayout();
    }
    void CloseBubble() { bubblePopup.IsOpen = false; lastBubblePlacement = null; measuredBubble = null; bubble.Visibility = Visibility.Collapsed; controller.CloseBubble(); SyncExpression(); }
    void Press(object sender, MouseButtonEventArgs e)
    {
        if (!NativeWindows.GetCursorPos(out var p)) return;
        pressScreen = new(p.X, p.Y); pressX = settings.OffsetX; pressY = settings.OffsetY; dragGesture = new(elapsed.Elapsed); dragging = true; moved = false; CaptureMouse(); e.Handled = true;
    }
    void Move(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed || target is not { } t || !NativeWindows.GetCursorPos(out var p)) return;
        var dx = (p.X - pressScreen.X) / t.Scale; var dy = (p.Y - pressScreen.Y) / t.Scale;
        if (dragGesture?.Update(elapsed.Elapsed, dx, dy) != true) return;
        moved = true; settings = settings with { OffsetX = pressX + dx, OffsetY = pressY + dy };
        var rect = CompanionPlacement.Place(t.Rect, t.Work, Width, Height, t.Scale, settings.OffsetX, settings.OffsetY);
        NativeWindows.SetWindowPos(handle, new nint(-1), (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, 0x10);
        lastPlacement = rect; PositionBubble();
    }
    void Release(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return; dragging = false; ReleaseMouseCapture(); e.Handled = true;
        if (moved) { Save(); return; }
        if (dragGesture?.IsClick(elapsed.Elapsed) != true) return;
        Interact();
    }
    void Interact(bool refresh = true)
    {
        var started = Stopwatch.GetTimestamp();
        Poke(); var kind = controller.Click();
        if (kind == BubbleKind.Greeting)
        {
            var line = greetings.TryGetValue(settings.Character + "/" + settings.Language, out var bag) ? bag.Next() : Local("我在这里陪你。", "I’m right here with you.", "ここで一緒にいるよ。");
            ShowMessage(CharacterName(settings.Character), line, 7); return;
        }
        ShowMessage(CharacterName(settings.Character), "", 12);
        RenderQuota();
        lastQuotaDisplayMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (refresh) _ = quota.RefreshAsync();
    }
    void Poke()
    {
        pokeInitial = new(pokeScale.ScaleX, pokeScale.ScaleY, pokeOffset.Y);
        pokeStarted = elapsed.Elapsed; pokeReduced = settings.ReducedMotion;
        AdvancePoke(TimeSpan.Zero);
        if (!validationMode) motionFrame.Start();
    }
    void AdvancePoke(TimeSpan age)
    {
        var pose = PokeMotion.Sample(age, pokeReduced, pokeInitial, displayHeight);
        pokeScale.ScaleX = pose.ScaleX; pokeScale.ScaleY = pose.ScaleY; pokeOffset.Y = pose.OffsetY;
        if (age >= PokeMotion.Duration(pokeReduced)) ResetPoke();
    }
    void ResetPoke()
    {
        pokeStarted = null;
        pokeScale.ScaleX = pokeScale.ScaleY = 1; pokeOffset.X = pokeOffset.Y = 0;
        StopMotionTimerIfIdle();
    }
    void ShowMessage(string title, string text, int seconds)
    { bubbleTitle.Text = title; bubbleText.Text = text; bubble.Visibility = Visibility.Visible; lifetime.Open(TimeSpan.FromSeconds(seconds)); SyncExpression(); UpdateWindowSize(); lastTick = elapsed.Elapsed; Tick(null, EventArgs.Empty); }
    string Local(string zh, string en, string ja) => settings.Language switch { "en" => en, "ja" => ja, _ => zh };
    string CharacterName(string value) => CharacterNames.Get(value);
    void OnQuotaReset(QuotaResetNotice notice) => Dispatcher.BeginInvoke(() =>
    {
        if (stopping || validationMode || !settings.QuotaResetNotifications || !quota.IsCurrentResetNotice(notice)) return;
        resetReminder?.Close();
        var foreground = NativeWindows.GetForegroundWindow();
        var work = target?.Work ?? NativeWindows.WorkArea(foreground);
        var dpi = NativeWindows.GetDpiForWindow(foreground);
        var scale = target?.Scale ?? (dpi > 0 ? dpi / 96d : 1d);
        var reminder = new QuotaResetReminder(settings.Language, notice, work, scale);
        resetReminder = reminder;
        reminder.Closed += (_, _) => { if (ReferenceEquals(resetReminder, reminder)) resetReminder = null; };
        reminder.Show();
    });
    void RenderQuota()
    {
        // Keep status/reconnect work silent; this string is only the sizing cache key.
        bubbleText.Text = quotaPanel.Update(quota.Snapshot, settings);
        bubbleTitle.Text = "";
        UpdateWindowSize();
    }
    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu(); ContextMenu = menu; menu.PlacementTarget = character;
        MenuItem Item(string label, Action action, bool check = false) { var item = new MenuItem { Header = label, IsCheckable = check, IsChecked = check }; item.Click += (_, _) => action(); return item; }
        var language = new MenuItem { Header = Local("语言", "Language", "言語") };
        foreach (var (value, label) in new[] { ("zh-CN", "简体中文"), ("en", "English"), ("ja", "日本語") }) language.Items.Add(Item(label, () => SetLanguage(value), settings.Language == value));
        var person = new MenuItem { Header = Local("角色", "Character", "キャラクター") };
        foreach (var value in CompanionSettings.Characters) person.Items.Add(Item(CharacterName(value), () => SetCharacter(value), settings.Character == value));
        var size = new MenuItem { Header = Local("大小", "Size", "サイズ") };
        foreach (var (value, height) in CompanionSettings.Sizes)
        {
            var label = value switch { "small" => Local("小杯", "Small", "小"), "large" => Local("大杯", "Large", "大"), "xlarge" => Local("超大杯", "Extra large", "特大"), _ => Local("中杯", "Medium", "中") };
            size.Items.Add(Item(label + " · " + height, () => { ResetPoke(); settings = settings with { Size = value }; LoadArt(); Save(); }, settings.Size == value));
        }
        menu.Items.Add(language); menu.Items.Add(person); menu.Items.Add(size); menu.Items.Add(new Separator());
        menu.Items.Add(Item(Local("主题…", "Theme…", "テーマ…"), ConfigureTheme));
        var refresh = new MenuItem { Header = Local("额度刷新间隔", "Quota refresh interval", "利用枠の更新間隔") };
        foreach (var seconds in new[] { 15, 30, 60, 120, 300 })
            refresh.Items.Add(Item(seconds + Local(" 秒", " seconds", " 秒"), () => SetRefreshInterval(seconds), settings.RefreshIntervalSeconds == seconds));
        refresh.Items.Add(new Separator());
        refresh.Items.Add(Item(Local("自定义…", "Custom…", "カスタム…") + " (" + settings.RefreshIntervalSeconds + "s)", ConfigureRefreshInterval));
        menu.Items.Add(refresh);
        menu.Items.Add(Item(Local("额度重置提醒", "Quota reset reminders", "利用枠リセット通知"), () =>
        {
            settings = settings with { QuotaResetNotifications = !settings.QuotaResetNotifications };
            if (!settings.QuotaResetNotifications) { resetReminder?.Close(); resetReminder = null; }
            Save();
        }, settings.QuotaResetNotifications));
        menu.Items.Add(Item(Local("减少动画", "Reduce motion", "アニメーションを減らす"), () => { settings = settings with { ReducedMotion = !settings.ReducedMotion }; Save(); }, settings.ReducedMotion));
        menu.Items.Add(Item(Local("立即刷新额度", "Refresh quota", "利用枠を更新"), () => _ = quota.RefreshAsync(true)));
        menu.Items.Add(Item(Local("重置位置", "Reset position", "位置をリセット"), () => { settings = settings with { OffsetX = 0, OffsetY = 0 }; Save(); }));
        menu.Items.Add(Item(Local("开机启动", "Start with Windows", "Windows 起動時に実行"), ToggleStartup, settings.Startup));
        menu.Items.Add(new Separator()); menu.Items.Add(Item(Local("退出", "Exit", "終了"), () => Application.Current.Shutdown()));
        return menu;
    }
    void SetRefreshInterval(int seconds)
    { settings = settings with { RefreshIntervalSeconds = Math.Clamp(seconds, 5, 3600) }; quota.RefreshInterval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds); Save(); }
    void ConfigureRefreshInterval()
    {
        settingsDialogOpen = true;
        try { var dialog = new RefreshIntervalDialog(this, settings.RefreshIntervalSeconds, settings.Language); if (dialog.ShowDialog() == true) SetRefreshInterval(dialog.Seconds); }
        finally { settingsDialogOpen = false; }
    }
    void ConfigureTheme()
    {
        settingsDialogOpen = true;
        try
        {
            var dialog = new ThemeDialog(this, settings);
            if (dialog.ShowDialog() != true) return;
            settings = settings with { QuotaTextColor = dialog.TextColor, QuotaBarColor = dialog.BarColor };
            Save(); if (controller.Bubble == BubbleKind.Quota) RenderQuota();
        }
        finally { settingsDialogOpen = false; }
    }
    void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var enabled = !settings.Startup;
            if (enabled) key.SetValue("CodexCompanion", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("CodexCompanion", false);
            settings = settings with { Startup = enabled }; Save();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { ShowMessage(Local("无法修改开机启动", "Startup could not be changed", "自動起動を変更できません"), e.GetType().Name, 7); }
    }
    void SetCharacter(string value) { controller.SetCharacter(value); settings = settings with { Character = value }; LoadArt(); Save(); if (controller.Bubble == BubbleKind.Quota) RenderQuota(); else RefreshGreeting(); }
    void SetLanguage(string value) { controller.SetLanguage(value); settings = settings with { Language = value }; Save(); if (controller.Bubble == BubbleKind.Quota) RenderQuota(); else RefreshGreeting(); }
    void RefreshGreeting()
    {
        if (controller.Bubble != BubbleKind.Greeting || !lifetime.IsOpen) return;
        bubbleTitle.Text = CharacterName(settings.Character);
        if (greetings.TryGetValue(settings.Character + "/" + settings.Language, out var bag)) bubbleText.Text = bag.Next();
        UpdateWindowSize();
    }
    internal void RenderPreview(string output, bool showQuota = false)
    {
        validationMode = true;
        bubbleTitle.Text = CharacterName(settings.Character);
        bubbleText.Text = greetings[settings.Character + "/" + settings.Language].Next();
        bubble.Visibility = Visibility.Visible;
        controller.Click(); if (!showQuota) controller.Click();
        lifetime.Open(TimeSpan.FromSeconds(showQuota ? 12 : 7)); SyncExpression(true);
        if (showQuota) RenderQuota();
        var image = RenderPreviewSurface();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.GetFullPath(output)); encoder.Save(stream);
    }
    internal Task<object> ExecuteAsync(CommandRequest request)
    {
        switch (request.Command)
        {
            case "show": manuallyHidden = false; break;
            case "hide": manuallyHidden = true; ResetPoke(); CompleteExpression(); bubblePopup.IsOpen = false; Hide(); break;
            case "exit":
                _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); await Dispatcher.InvokeAsync(() => Application.Current.Shutdown()); });
                break;
            case "set-character": SetCharacter(request.Value!); break;
            case "set-language": SetLanguage(request.Value!); break;
            case "set-refresh-interval": SetRefreshInterval(int.Parse(request.Value!, CultureInfo.InvariantCulture)); break;
            case "refresh-quota": _ = quota.RefreshAsync(true); break;
        }
        using var process = Process.GetCurrentProcess();
        return Task.FromResult<object>(new { ok = true, version = typeof(CompanionWindow).Assembly.GetName().Version?.ToString(3), visible = IsVisible, codexVisible = target is not null, character = settings.Character, displayName = CharacterName(settings.Character), language = settings.Language, size = settings.Size, displayHeight, reducedMotion = settings.ReducedMotion, startup = settings.Startup, refreshIntervalSeconds = settings.RefreshIntervalSeconds, quotaResetNotifications = settings.QuotaResetNotifications, referencePreview = artPreview, expression = expressionIndex switch { 1 => "greeting", 2 => "quota", _ => "idle" }, transitioning = expressionStarted is not null, lastQuotaDisplayMs, position = lastPlacement, bubblePosition = lastBubblePlacement, quota = quota.Snapshot, memory = new { workingSetMB = Math.Round(process.WorkingSet64 / 1048576d, 1), privateMB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1), decodedFrames = artFrames?.Distinct().Count() ?? 0, retainedArtBytes = RetainedArtBytes } });
    }
}
