using CodexCompanion.Core;
public static class RuntimeTests
{
    public static void Run()
    {
        var p = CompanionPlacement.Place(new(100, 200, 1200, 900), new(0, 0, 1920, 1080), 200, 240, 1.5, 0, 0);
        if (p.X != 970 || p.Y != 710) throw new Exception("20 DIP inset must scale with target DPI.");
        var clamped = CompanionPlacement.Place(new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), 200, 240, 1, 5000, 5000);
        if (clamped.X != -200 || clamped.Y != 800) throw new Exception("Dragging must remain within the target monitor work area.");
        var compactWork = new PixelRect(0, 0, 1366, 768);
        var oversized = CompanionPlacement.Place(compactWork, compactWork, 432, 432, 2, 0, 0);
        if (oversized.Width > compactWork.Width || oversized.Height > compactWork.Height || oversized.X < compactWork.X || oversized.Y < compactWork.Y || oversized.X + oversized.Width > compactWork.X + compactWork.Width || oversized.Y + oversized.Height > compactWork.Y + compactWork.Height)
            throw new Exception("An ultra-size companion at 200% DPI must fit entirely inside a 1366×768 work area.");
        var negativeWork = new PixelRect(-1920, -1080, 1920, 1080);
        var highDpi = CompanionPlacement.Place(negativeWork, negativeWork, 432, 432, 3, 5000, -5000);
        if (highDpi.Width > 1080 || highDpi.Height > 1080 || highDpi.X < -1920 || highDpi.Y < -1080 || highDpi.X + highDpi.Width > 0 || highDpi.Y + highDpi.Height > 0)
            throw new Exception("Oversized placement must stay inside a negative-coordinate monitor at 300% DPI.");
        var wide = CompanionPlacement.Place(compactWork, compactWork, 800, 400, 2, 0, 0);
        if (wide.Width > compactWork.Width || wide.Height > compactWork.Height || wide.Width != wide.Height * 2)
            throw new Exception("Defensive window fitting must preserve the requested aspect ratio.");
        var timer = new BubbleLifetime(); timer.Open(TimeSpan.FromSeconds(7)); timer.Advance(TimeSpan.FromSeconds(6), true);
        if (!timer.IsOpen || timer.Remaining != TimeSpan.FromSeconds(7)) throw new Exception("Hover pauses expiry.");
        timer.Advance(TimeSpan.FromSeconds(6), false); if (!timer.IsOpen) throw new Exception("Bubble closed early.");
        timer.Advance(TimeSpan.FromSeconds(1), false); if (timer.IsOpen) throw new Exception("Bubble did not expire.");
        if (CompanionCommand.Validate("set-character", "cute") is not null || CompanionCommand.Validate("set-character", "bad") is null || CompanionCommand.Validate("exec", "anything") is null)
            throw new Exception("IPC must accept only known commands and valid values.");
        if (CompanionCommand.Validate("exit", null) is not null || CompanionCommand.Validate("exit", "anything") is null)
            throw new Exception("Exit is a fixed command without additional arguments.");
        Console.WriteLine("PASS placement/DPI/clamping, hover lifetime, IPC validation");
        BubbleDurationTests.Run();
    }
}
