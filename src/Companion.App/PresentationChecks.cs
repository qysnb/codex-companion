using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal sealed partial class CompanionWindow
{
    RenderTargetBitmap RenderPreviewSurface()
    {
        bubblePopup.Child = null;
        try
        {
            measuredBubble = null; MeasureBubble();
            var panel = new Grid { Width = Math.Max(bubble.Width, settings.Height + 12), Height = settings.Height + bubble.DesiredSize.Height + 6 };
            Image Actor(Image layer) => new() { Source = layer.Source, Opacity = layer.Opacity, Height = settings.Height, Width = settings.Height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = layer.RenderTransformOrigin, RenderTransform = layer.RenderTransform.Clone() };
            panel.Children.Add(bubble); panel.Children.Add(Actor(departing)); panel.Children.Add(Actor(character));
            panel.Measure(new Size(panel.Width, panel.Height)); panel.Arrange(new Rect(0, 0, panel.Width, panel.Height)); panel.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(panel.Width), (int)Math.Ceiling(panel.Height), 96, 96, PixelFormats.Pbgra32); image.Render(panel);
            panel.Children.Remove(bubble); return image;
        }
        finally { bubblePopup.Child = bubble; }
    }

    void SettleMotion()
    {
        if (pokeStarted is not null) AdvancePoke(PokeMotion.Duration(pokeReduced));
        if (expressionStarted is not null) AdvanceExpression(ExpressionMotion.Duration(expressionReduced));
    }

    void AssertExpression(BubbleKind kind)
    {
        var expected = artFrames![ExpressionMotion.FrameIndex(kind)];
        if (!ReferenceEquals(shownFrame, expected) || !ReferenceEquals(character.Source, expected.Image))
            throw new InvalidOperationException("The active state is not using its complete registered character frame.");
    }

    static byte[] ImageBytes(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }

    internal async Task<object> VerifyPresentationAsync(bool synthetic, string? previewPath = null)
    {
        validationMode = true; Topmost = false;
        // Use only our own off-screen windows. The caller labels quota data.
        Left = 11000; Top = 11000; Show(); UpdateLayout();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        decodedScale = scale; LoadArt(); UpdateLayout();
        NativeWindows.GetWindowRect(handle, out var original);
        var bounds = original.Pixels;
        target = (handle, bounds, new(bounds.X - 1024, bounds.Y - 1024, 2048, 2048), scale);
        var timings = new List<double>(); var gaps = new List<double>();
        var sources = new HashSet<ImageSource?> { character.Source };
        for (var i = 0; i < 10; i++)
        {
            var timer = Stopwatch.StartNew(); Interact(false);
            AssertExpression(i % 2 == 0 ? BubbleKind.Quota : BubbleKind.Greeting); sources.Add(character.Source);
            bubblePopup.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PositionBubble();
            NativeWindows.GetWindowRect(handle, out var after);
            if (after.Pixels != bounds) throw new InvalidOperationException("A state transition changed the character window.");
            var source = (HwndSource)PresentationSource.FromVisual(bubble);
            NativeWindows.GetWindowRect(source.Handle, out var popupBounds);
            var top = PointToScreen(new Point(ActualWidth - character.ActualWidth, ActualHeight - character.ActualHeight));
            var gap = (top.Y - popupBounds.Bottom) / scale;
            if (Math.Abs(gap - 6) > 1) throw new InvalidOperationException("Bubble gap must remain six DIP.");
            gaps.Add(gap);
            if (i % 2 == 0) timings.Add(timer.Elapsed.TotalMilliseconds);
            SettleMotion();
        }
        if (sources.Count != 3) throw new InvalidOperationException($"Expected three complete expression images; observed {sources.Count}.");
        CloseBubble(); SettleMotion(); AssertExpression(BubbleKind.None);
        if (timings.Max() >= 150) throw new InvalidOperationException($"Cached quota UI exceeded 150ms: {timings.Max():0.0}ms.");

        var scenarios = new List<object>(); var previews = new List<BitmapSource>();
        var saved = settings;
        foreach (var person in CompanionSettings.Characters)
        foreach (var language in CompanionSettings.Languages)
        foreach (var (size, _) in CompanionSettings.Sizes)
        {
            CloseBubble(); settings = saved with { Character = person, Language = language, Size = size };
            controller.SetCharacter(person); controller.SetLanguage(language); LoadArt(); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeWindows.GetWindowRect(handle, out var anchored);
            AssertExpression(BubbleKind.None);
            Interact(false); AssertExpression(BubbleKind.Quota); bubblePopup.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); PositionBubble(); SettleMotion();
            var quotaHeight = bubble.ActualHeight;
            lifetime.Advance(TimeSpan.FromSeconds(13), false);
            if (lifetime.IsOpen) throw new InvalidOperationException("Quota bubble did not auto-expire.");
            CloseBubble(); SettleMotion(); AssertExpression(BubbleKind.None);
            // Fault injection: a retained tall layout must not survive reopening.
            bubble.Height = 215;
            Interact(false); AssertExpression(BubbleKind.Greeting);
            bubbleText.Text = Local("你好呀。", "Hello there.", "こんにちは。");
            bubblePopup.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); PositionBubble(); SettleMotion();
            var source = (HwndSource)PresentationSource.FromVisual(bubble);
            NativeWindows.GetWindowRect(source.Handle, out var popupBounds);
            var actorTop = PointToScreen(new Point(ActualWidth - character.ActualWidth, ActualHeight - character.ActualHeight)).Y;
            var paintedGap = (actorTop - bubble.PointToScreen(new Point(0, bubble.ActualHeight)).Y) / scale;
            var unusedHeight = (popupBounds.Bottom - popupBounds.Top) / scale - bubble.ActualHeight;
            if (Math.Abs(paintedGap - 6) > 1 || Math.Abs(unusedHeight) > 1 || bubble.ActualHeight >= quotaHeight || bubble.ActualHeight > 105)
                throw new InvalidOperationException($"Greeting layout regression ({person}/{language}/{size}): gap={paintedGap:0.0}, unused={unusedHeight:0.0}, greeting={bubble.ActualHeight:0.0}, quota={quotaHeight:0.0} DIP.");
            foreach (var reduced in new[] { false, true })
            {
                settings = settings with { ReducedMotion = reduced }; Poke();
                AdvancePoke(PokeMotion.Duration(reduced) * .22); AssertExpression(BubbleKind.Greeting);
                if (pokeScale.ScaleX <= 1 || pokeScale.ScaleY >= 1 || !ReferenceEquals(departing.RenderTransform, character.RenderTransform)) throw new InvalidOperationException("Both whole-image layers must share the poke transform.");
                var transformed = character.TransformToAncestor((Visual)Content).TransformBounds(new Rect(0, 0, character.ActualWidth, character.ActualHeight));
                if (transformed.Left < 0 || transformed.Top < 0 || transformed.Right > ActualWidth + .01 || transformed.Bottom > ActualHeight + .01) throw new InvalidOperationException("Poke clipped the character.");
                var beforeRestart = character.RenderTransform.Value; Poke();
                if (character.RenderTransform.Value != beforeRestart) throw new InvalidOperationException("A rapid repeated poke jumped to a different starting pose.");
                PositionBubble();
                if (Math.Abs((actorTop - bubble.PointToScreen(new Point(0, bubble.ActualHeight)).Y) / scale - paintedGap) > .1) throw new InvalidOperationException("Poke moved the bubble anchor.");
                SettleMotion();
                if (!character.RenderTransform.Value.IsIdentity || motionFrame.IsEnabled) throw new InvalidOperationException("Poke did not restore and stop.");
            }
            NativeWindows.GetWindowRect(handle, out var after);
            if (after.Pixels != anchored.Pixels) throw new InvalidOperationException("Interaction moved the saved character anchor.");
            scenarios.Add(new { person, language, size, quotaHeightDip = quotaHeight, greetingHeightDip = bubble.ActualHeight, paintedGapDip = paintedGap, unusedHeightDip = unusedHeight, threeStatesVerified = true });
            lifetime.Advance(TimeSpan.FromSeconds(8), false);
            if (lifetime.IsOpen) throw new InvalidOperationException("Greeting bubble did not auto-expire.");
            CloseBubble(); SettleMotion(); AssertExpression(BubbleKind.None);
            if (previewPath is not null && language == "zh-CN" && size == "medium")
            {
                for (var index = 0; index < 3; index++)
                {
                    SetFrame(artFrames![index]);
                    bubbleTitle.Text = CharacterName(person);
                    bubbleText.Text = new[] { "待机 · 闭嘴", "问候 · 眯眼", "用量 · 张嘴" }[index]; bubble.Visibility = Visibility.Visible;
                    previews.Add(RenderPreviewSurface());
                }
                SyncExpression(true);
            }
        }

        // Interrupt an in-progress expression repeatedly and require the first
        // replacement image to be exactly the previous visible composite.
        CloseBubble(); SettleMotion();
        var cacheBytes = artFrames!.Sum(item => item.Bytes); long peakArtBytes = cacheBytes;
        for (var i = 0; i < 32; i++)
        {
            controller.Click(); lifetime.Open(TimeSpan.FromSeconds(12)); SyncExpression();
            AdvanceExpression(ExpressionMotion.Duration(expressionReduced) * .43);
            var before = SnapshotExpression();
            controller.Click(); SyncExpression();
            if (outgoingFrame is null || !ImageBytes(before.Image).SequenceEqual(ImageBytes(outgoingFrame.Image))) throw new InvalidOperationException("Interrupted expression did not continue from the visible image.");
            peakArtBytes = Math.Max(peakArtBytes, RetainedArtBytes);
            if (RetainedArtBytes > cacheBytes + artFrames![0].Bytes || artFrames.Length != 3) throw new InvalidOperationException("Expression snapshots accumulated instead of being replaced.");
            AdvanceExpression(ExpressionMotion.Duration(expressionReduced) * .43);
        }
        CloseBubble(); SettleMotion(); AssertExpression(BubbleKind.None);
        if (RetainedArtBytes != cacheBytes || departing.Source is not null || character.Opacity != 1) throw new InvalidOperationException("Transition resources remained after completion.");

        // Test actual dispatcher ticks in both modes without inspecting host UI.
        foreach (var reduced in new[] { false, true })
        {
            settings = settings with { ReducedMotion = reduced };
            bubble.Visibility = Visibility.Visible; bubblePopup.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); PositionBubble();
            var popup = (HwndSource)PresentationSource.FromVisual(bubble);
            NativeWindows.GetWindowRect(popup.Handle, out var bubbleBefore);
            NativeWindows.GetWindowRect(handle, out var actorBefore);
            validationMode = false;
            controller.Click(); lifetime.Open(TimeSpan.FromSeconds(12)); Poke(); SyncExpression();
            await Task.Delay(65);
            if (character.RenderTransform.Value.IsIdentity) throw new InvalidOperationException("Click did not start timed motion.");
            NativeWindows.GetWindowRect(popup.Handle, out var bubbleDuring);
            NativeWindows.GetWindowRect(handle, out var actorDuring);
            if (bubbleBefore.Pixels != bubbleDuring.Pixels || actorBefore.Pixels != actorDuring.Pixels) throw new InvalidOperationException("Timed transition moved a native window.");
            controller.Click(); Poke(); SyncExpression();
            await Task.Delay(PokeMotion.Duration(reduced) + TimeSpan.FromMilliseconds(120));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            validationMode = true;
            if (!character.RenderTransform.Value.IsIdentity || motionFrame.IsEnabled || outgoingFrame is not null || expressionStarted is not null || character.Opacity != 1) throw new InvalidOperationException("Timed transitions did not restore, clean up and stop.");
            AssertExpression(controller.Bubble);
        }
        CloseBubble(); SettleMotion();
        // Exercise actual WPF sizing and side placement on a constrained work
        // area at the machine's real DPI, rather than faking another DPI.
        var savedTarget = target; var previousSettings = settings;
        NativeWindows.GetWindowRect(handle, out var beforeFit);
        target = (handle, beforeFit.Pixels, new(beforeFit.Left, beforeFit.Top, 1000 * scale, 312 * scale), scale);
        settings = settings with { Size = "xlarge" }; LoadArt(); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        NativeWindows.GetWindowRect(handle, out var fittedBounds);
        if (Math.Abs(displayHeight - 300) > .01 || settings.Height != 420 || settings.Size != "xlarge" || fittedBounds.Bottom - fittedBounds.Top > 312 * scale + 1 || character.ActualHeight != displayHeight)
            throw new InvalidOperationException("The actual oversized WPF character did not fit without changing the saved preset.");
        Interact(false); SettleMotion(); bubblePopup.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); PositionBubble();
        var sideSource = (HwndSource)PresentationSource.FromVisual(bubble);
        NativeWindows.GetWindowRect(sideSource.Handle, out var sidePopup);
        var actorOrigin = PointToScreen(new Point(ActualWidth - character.ActualWidth, ActualHeight - character.ActualHeight));
        var actorPixels = new Rect(actorOrigin, new Size(character.ActualWidth * scale, character.ActualHeight * scale));
        if (actorPixels.IntersectsWith(new Rect(sidePopup.Left, sidePopup.Top, sidePopup.Right - sidePopup.Left, sidePopup.Bottom - sidePopup.Top))) throw new InvalidOperationException("The constrained-work-area bubble covered the character instead of moving beside it.");
        CloseBubble(); SettleMotion(); settings = previousSettings; target = savedTarget; LoadArt(); UpdateLayout();
        ReleaseArt();
        if (RetainedArtBytes != 0 || character.Source is not null || departing.Source is not null || motionFrame.IsEnabled) throw new InvalidOperationException("Hidden art release retained an image.");
        LoadArt(); AssertExpression(BubbleKind.None);
        if (previewPath is not null)
        {
            var previewHeight = CompanionSettings.Characters.Length * 360;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 32, 42)), null, new Rect(0, 0, 990, previewHeight));
                for (var i = 0; i < previews.Count; i++) context.DrawImage(previews[i], new Rect(i % 3 * 330, i / 3 * 360, previews[i].PixelWidth, previews[i].PixelHeight));
            }
            var image = new RenderTargetBitmap(990, previewHeight, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.GetFullPath(previewPath)); encoder.Save(stream);
        }
        CloseBubble(); SettleMotion(); Hide();
        var reminderScenarios = VerifyResetReminders();
        var quotaThemeScenarios = VerifyQuotaThemes(previewPath);
        return new { ok = true, source = synthetic ? "synthetic cached quota UI, not a network benchmark" : "prewarmed real quota cached UI", systemDpiScale = scale, interactions = 10, expressionFramesUsed = sources.Count, characterPositionUnchanged = true, bubbleGapsDip = gaps, cachedQuotaDisplayMilliseconds = timings, maximumMilliseconds = timings.Max(), autoDismissReopenScenarios = scenarios, staleHeightInjected = true, realTimerAndRepeatedClickRestore = true, interruptedTransitions = 32, exactCompositeContinuity = true, cacheBytes, peakArtBytes, fittedDisplayHeight = 300, sideBubbleVerified = true, cacheReleasedAndReloaded = true, reminderScenarios, quotaThemeScenarios, previewPath };
    }

    int VerifyQuotaThemes(string? previewPath)
    {
        var count = 0;
        foreach (var language in CompanionSettings.Languages)
        foreach (var percent in new double?[] { 0, 67, 100, null })
        foreach (var colors in new[] { ("#3D2F42", "#00A9D6"), ("#513A63", "#9871C6") })
        {
            var panel = new QuotaPanel();
            var theme = new CompanionSettings { Language = language, QuotaTextColor = colors.Item1, QuotaBarColor = colors.Item2 };
            var snapshot = new QuotaSnapshot(QuotaStatus.Live, percent is {} p ? new(p, 300, DateTimeOffset.Now.AddMinutes(97)) : null,
                percent is {} w ? new(w, 10080, DateTimeOffset.Now.AddDays(6).AddHours(17)) : null, DateTimeOffset.Now);
            panel.Update(snapshot, theme);
            var width = QuotaPanel.BubbleWidth(language) - 36;
            panel.Measure(new Size(width, double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
            foreach (var row in new[] { panel.FiveHour, panel.Weekly })
            {
                foreach (var label in row.Labels)
                {
                    var text = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, label.Foreground, 1);
                    if (text.Width > label.ActualWidth + 1) throw new InvalidOperationException($"Quota label clipped: {language}/{label.Text}/{text.Width:0.0}/{label.ActualWidth:0.0}");
                }
                var expected = row.Track.ActualWidth * (percent ?? 0) / 100;
                if (Math.Abs(row.Fill.ActualWidth - expected) > 1 || row.Track.ActualHeight != 4)
                    throw new InvalidOperationException("Quota bar does not represent the remaining percentage.");
                if (((SolidColorBrush)row.Fill.Background).Color != (Color)ColorConverter.ConvertFromString(colors.Item2) ||
                    ((SolidColorBrush)row.Labels[0].Foreground).Color != (Color)ColorConverter.ConvertFromString(colors.Item1))
                    throw new InvalidOperationException("Custom quota colors were not applied.");
            }
            if (previewPath is not null && language == "zh-CN" && percent == 67 && colors.Item2 == "#00A9D6")
            {
                var surfaceWidth = QuotaPanel.BubbleWidth(language);
                var surface = new Border { Child = panel, Width = surfaceWidth, Padding = new(17, 14, 17, 14), Background = new SolidColorBrush(Color.FromRgb(255, 250, 255)), CornerRadius = new(17) };
                surface.Measure(new Size(surfaceWidth, double.PositiveInfinity)); surface.Arrange(new Rect(0, 0, surfaceWidth, surface.DesiredSize.Height)); surface.UpdateLayout();
                var image = new RenderTargetBitmap((int)Math.Ceiling(surfaceWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.ChangeExtension(previewPath, ".quota.png")); encoder.Save(output);
            }
            count++;
        }
        foreach (var language in CompanionSettings.Languages)
        {
            var dialog = new ThemeDialog(this, new() { Language = language });
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(540, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, 540, content.DesiredSize.Height)); content.UpdateLayout();
            if (content.DesiredSize.Height > 600 || dialog.TextColor != "#3D2F42" || dialog.BarColor != "#00A9D6")
                throw new InvalidOperationException("Theme dialog layout or defaults are invalid.");
            dialog.Close();
        }
        return count;
    }

    static int VerifyResetReminders()
    {
        var count = 0;
        foreach (var language in CompanionSettings.Languages)
        foreach (var notice in new[] { new QuotaResetNotice(true, false), new(false, true), new(true, true) })
        {
            var reminder = new QuotaResetReminder(language, notice, new(0, 0, 1920, 1080), 1);
            var content = (FrameworkElement)reminder.Content;
            content.Measure(new Size(360, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, 360, content.DesiredSize.Height)); content.UpdateLayout();
            if (content.DesiredSize.Height is < 70 or > 220 || reminder.ShowActivated || reminder.ShowInTaskbar || !reminder.Topmost)
                throw new InvalidOperationException("Reset reminder sizing or non-activation configuration is invalid.");
            // Rendering all locales exercises actual glyph shaping without showing a synthetic alert.
            var image = new RenderTargetBitmap(360, (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32); image.Render(content);
            reminder.Close(); count++;
        }
        return count;
    }
}

internal sealed class PresentationQuota : ICodexConnection
{
    public event Action<string, JsonElement>? Notification { add { } remove { } }
    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var json = method == "account/read" ? """{"account":{"type":"chatgpt","email":"synthetic@example.test"}}""" : """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1793000000},"secondary":{"usedPercent":50,"windowDurationMins":10080,"resetsAt":1793500000}}}""";
        using var document = JsonDocument.Parse(json); return Task.FromResult(document.RootElement.Clone());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
