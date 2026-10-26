using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal sealed partial class CompanionWindow
{
    CharacterFrame[]? artFrames;
    CharacterFrame? shownFrame, outgoingFrame;
    TimeSpan? expressionStarted;
    bool expressionReduced;
    int expressionIndex;

    void SyncExpression(bool immediate = false)
    {
        if (artFrames is null) return;
        var index = ExpressionMotion.FrameIndex(lifetime.IsOpen ? controller.Bubble : BubbleKind.None);
        var next = artFrames[index]; expressionIndex = index;
        if (ReferenceEquals(next, shownFrame)) return;
        if (immediate || shownFrame is null)
        {
            CompleteExpression(); SetFrame(next); return;
        }
        // During rapid clicks take one snapshot of the current untransformed
        // blend. Replace it on the next interruption; never accumulate layers.
        var previous = outgoingFrame is null ? shownFrame : SnapshotExpression();
        outgoingFrame = previous; departing.Source = previous.Image;
        SetFrame(next); character.Opacity = 0;
        expressionReduced = settings.ReducedMotion; expressionStarted = elapsed.Elapsed;
        if (!validationMode) motionFrame.Start();
    }

    void SetFrame(CharacterFrame asset)
    {
        shownFrame = asset; character.Source = pixelsSource = asset.Image; pixels = asset.Alpha;
    }

    CharacterFrame SnapshotExpression()
    {
        var current = shownFrame!;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, current.Image.PixelWidth, current.Image.PixelHeight);
            drawing.DrawImage(outgoingFrame!.Image, rect);
            drawing.PushOpacity(character.Opacity); drawing.DrawImage(current.Image, rect); drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(current.Image.PixelWidth, current.Image.PixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        return CharacterFrame.FromImage(bitmap);
    }

    void AdvanceExpression(TimeSpan age)
    {
        if (expressionStarted is null) return;
        // Keep the outgoing body opaque while revealing the registered next
        // frame. A two-sided opacity fade would dim the entire character.
        character.Opacity = ExpressionMotion.Opacity(age, expressionReduced);
        if (age >= ExpressionMotion.Duration(expressionReduced)) CompleteExpression();
    }

    void CompleteExpression()
    {
        expressionStarted = null; outgoingFrame = null; departing.Source = null; character.Opacity = 1;
        StopMotionTimerIfIdle();
    }

    void AnimateFrame()
    {
        var now = elapsed.Elapsed;
        if (pokeStarted is { } pokeTime) AdvancePoke(now - pokeTime);
        if (expressionStarted is { } expressionTime) AdvanceExpression(now - expressionTime);
        StopMotionTimerIfIdle();
    }

    void StopMotionTimerIfIdle()
    {
        if (pokeStarted is null && expressionStarted is null) motionFrame.Stop();
    }

    void ReleaseArt()
    {
        ResetPoke(); CompleteExpression();
        character.Source = null; shownFrame = null; artFrames = null;
        idle = pixelsSource = null; pixels = null;
    }

    long RetainedArtBytes => (artFrames?.Sum(item => item.Bytes) ?? 0) +
        (outgoingFrame is not null && artFrames?.Any(item => ReferenceEquals(item, outgoingFrame)) != true ? outgoingFrame.Bytes : 0);
}
