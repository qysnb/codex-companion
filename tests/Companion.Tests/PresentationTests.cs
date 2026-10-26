using CodexCompanion.Core;

public static class PresentationTests
{
    public static void Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(CharacterNames.Get("elegant") == "Astra" && CharacterNames.Get("mischievous") == "Sol" && CharacterNames.Get("cute") == "Luna", "Only display names change; existing IDs keep their mapping.");
        Check(new CompanionSettings().RefreshIntervalSeconds == 30 && (new CompanionSettings { RefreshIntervalSeconds = 1 }).Normalize().RefreshIntervalSeconds == 30, "Refresh defaults to 30 seconds and unsafe persisted intervals recover to the default.");
        Check((new CompanionSettings { RefreshIntervalSeconds = 5 }).Normalize().RefreshIntervalSeconds == 5 && (new CompanionSettings { RefreshIntervalSeconds = 3600 }).Normalize().RefreshIntervalSeconds == 3600, "Custom refresh interval endpoints must be supported.");
        Check(CompanionCommand.Validate("set-refresh-interval", "60") is null && CompanionCommand.Validate("set-refresh-interval", "4") is not null && CompanionCommand.Validate("set-refresh-interval", "3601") is not null, "IPC refresh intervals must use the same bounds as settings.");
        var character = new PixelRect(900, 600, 240, 240); var work = new PixelRect(0, 0, 1920, 1080);
        var greeting = BubblePlacement.Place(character, work, 320, 74, 1);
        var quota = BubblePlacement.Place(character, work, 320, 200, 1);
        Check(greeting.Y + greeting.Height == 594 && quota.Y + quota.Height == 594, "Short and tall bubbles must share the same six-pixel gap above the character.");
        var edge = BubblePlacement.Place(new(0, 20, 240, 240), work, 320, 200, 1);
        Check(edge.X >= 0 && edge.Y >= 0 && character == new PixelRect(900, 600, 240, 240), "Bubble clamping must not change the character rectangle.");
        var smallWork = new PixelRect(0, 0, 1000, 600);
        var ultra = new PixelRect(568, 168, 420, 420);
        var beside = BubblePlacement.Place(ultra, smallWork, 320, 215, 1);
        Check(beside.X + beside.Width == ultra.X - 6 && beside.Y >= 0 && beside.Y + beside.Height <= 600, "If neither vertical position fits, an ultra-size character must use a bubble beside it with the same six-pixel gap.");
        var leftUltra = new PixelRect(12, 120, 420, 420);
        var rightBubble = BubblePlacement.Place(leftUltra, smallWork, 320, 215, 1);
        Check(rightBubble.X == leftUltra.X + leftUltra.Width + 6, "The bubble must use the right side when only that side fits.");
        var below = BubblePlacement.Place(new(568, 20, 420, 420), new(0, 0, 1000, 800), 320, 215, 1);
        Check(below.Y == 446 && below.X == 668, "A fitting bubble below the character must keep priority over side placement.");
        var scaledUltra = new PixelRect(-648, 252, 630, 630);
        var scaledSide = BubblePlacement.Place(scaledUltra, new(-1800, 0, 1800, 900), 480, 322.5, 1.5);
        Check(scaledSide.X + scaledSide.Width == scaledUltra.X - 9 && scaledSide.Y + scaledSide.Height <= 900, "Side placement must preserve the DPI-scaled gap on monitors with negative coordinates.");
        Check(ultra == new PixelRect(568, 168, 420, 420), "Placing a bubble must never move the ultra-size character.");
        var drag = new LongPressDrag(TimeSpan.Zero);
        Check(!drag.Update(TimeSpan.FromMilliseconds(349), 20, 0), "A short swipe must not move the character.");
        Check(!drag.IsClick(TimeSpan.FromMilliseconds(349)), "A short swipe must not trigger a click.");
        Check(drag.Update(TimeSpan.FromMilliseconds(350), 20, 0), "Only a held press may begin dragging.");
        Check(!drag.IsClick(TimeSpan.FromMilliseconds(500)), "Dragging must not advance click parity.");
        var click = new LongPressDrag(TimeSpan.Zero);
        Check(!click.Update(TimeSpan.FromMilliseconds(60), 2, 1) && click.IsClick(TimeSpan.FromMilliseconds(100)), "A normal click tolerates small pointer jitter.");
        foreach (var reduced in new[] { false, true })
        {
            var ultraPeak = PokeMotion.Sample(PokeMotion.Duration(reduced) * .22, reduced, 420);
            Check((ultraPeak.ScaleX - 1) * 420 <= 12.000001, "A 420 DIP character must keep its horizontal poke inside the existing 12 DIP window margin.");
            foreach (var height in new[] { 180, 240, 320 })
            {
                var oldPeak = PokeMotion.Sample(PokeMotion.Duration(reduced) * .22, reduced, height);
                Check(oldPeak.ScaleX == 1 + (reduced ? .012 : .035) && oldPeak.ScaleY == 1 - (reduced ? .03 : .10), "Adding ultra size must preserve the existing three sizes' full animation.");
            }
            for (var ms = 0; ms <= 500; ms++)
            {
                var pose = PokeMotion.Sample(TimeSpan.FromMilliseconds(ms), reduced, 420);
                var repeatedPose = PokeMotion.Sample(TimeSpan.FromMilliseconds(ms), reduced, ultraPeak, 420);
                Check((pose.ScaleX - 1) * 420 <= 12.000001 && (repeatedPose.ScaleX - 1) * 420 <= 12.000001, "Single and repeated ultra-size pokes must remain inside the window throughout the animation.");
            }
            Check(PokeMotion.Sample(TimeSpan.Zero, reduced, ultraPeak, 420) == ultraPeak && PokeMotion.Sample(PokeMotion.Duration(reduced), reduced, ultraPeak, 420) == PokePose.Rest, "Repeated ultra-size clicks must start at the current pose and return exactly to rest.");
            Check(ExpressionMotion.FrameIndex(BubbleKind.None) == 0 && ExpressionMotion.FrameIndex(BubbleKind.Greeting) == 1 && ExpressionMotion.FrameIndex(BubbleKind.Quota) == 2, "Idle, greeting and quota must use their three existing whole images.");
            double previous = 0;
            for (var ms = 0; ms <= 200; ms++)
            {
                var opacity = ExpressionMotion.Opacity(TimeSpan.FromMilliseconds(ms), reduced);
                Check(opacity >= previous && opacity is >= 0 and <= 1, "Expression opacity must progress continuously without overshoot.");
                previous = opacity;
            }
            Check(ExpressionMotion.Opacity(TimeSpan.Zero, reduced) == 0 && ExpressionMotion.Opacity(ExpressionMotion.Duration(reduced), reduced) == 1, "Expression transitions must finish exactly, including reduced motion.");
            Check(PokeMotion.Sample(TimeSpan.Zero, reduced) == PokePose.Rest && PokeMotion.Sample(TimeSpan.FromSeconds(1), reduced) == PokePose.Rest, "Click motion must restore the exact resting geometry.");
            var peak = PokeMotion.Sample(PokeMotion.Duration(reduced) * .22, reduced);
            Check(peak.ScaleX > 1 && peak.ScaleY < 1, "A left click must deform the whole character, also in reduced motion mode.");
            Check(PokeMotion.Sample(TimeSpan.Zero, reduced, peak) == peak && PokeMotion.Sample(PokeMotion.Duration(reduced), reduced, peak) == PokePose.Rest, "A repeated click must continue from the current deformation and still restore exactly.");
            for (var ms = 0; ms <= 500; ms++)
            {
                var pose = PokeMotion.Sample(TimeSpan.FromMilliseconds(ms), reduced);
                Check(pose.ScaleX is >= 1 and <= 1.036 && pose.ScaleY is >= .899 and <= 1 && pose.OffsetY is >= -1.501 and <= 0, "Poke geometry must stay bounded and must never move the saved anchor.");
                Check((pose.ScaleX - 1) * 320 <= 12, "The largest character must fit inside its existing window during a poke.");
            }
            Check(PokeMotion.Sample(PokeMotion.Duration(reduced), reduced) == PokePose.Rest, "The end frame must have no residual displacement or deformation.");
        }
        Console.WriteLine("PASS character names, constant bubble gap, ultra-size side placement, long-press drag, size-aware poke bounds");
    }
}
