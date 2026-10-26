using CodexCompanion.Core;

public static class ResourceTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Check(ResourcePolicy.DecodeHeight(240, 1) == 240, "Decode at displayed size at 100% DPI.");
        Check(ResourcePolicy.DecodeHeight(320, 2) == 640, "High DPI must retain enough image detail.");
        Check(ResourcePolicy.DecodeHeight(320, 8) <= 960, "Decode size must be bounded regardless of source size or DPI.");
        var requested = new CompanionSettings { Size = "xlarge", OffsetX = -200, OffsetY = 50 };
        var compactWork = new PixelRect(0, 0, 1366, 768);
        var fitted = ResourcePolicy.FitDisplayHeight(requested.Height, compactWork, 2);
        Check(fitted == 372 && (fitted + 12) * 2 == 768, "The rendered character plus its 12 DIP margin must fit a 768-pixel work area at 200% DPI.");
        Check(ResourcePolicy.DecodeHeight(fitted, 2) == 744, "Art must decode at the fitted display height rather than the persisted requested height.");
        Check(requested.Height == 420 && requested.Size == "xlarge" && requested.OffsetX == -200 && requested.OffsetY == 50, "Fitting must leave the requested size and saved offsets unchanged.");
        Check(ResourcePolicy.FitDisplayHeight(420, new(-1920, -1080, 1920, 1080), 3) == 348, "A negative-coordinate 1080-pixel monitor at 300% DPI must fit the full character and margin.");
        Check(ResourcePolicy.FitDisplayHeight(420, new(-768, 0, 768, 1366), 2) == 372, "A portrait monitor must fit against its narrower dimension.");
        Check(ResourcePolicy.FitDisplayHeight(420, compactWork, 1) == 420, "The ultra size must remain full size when it fits.");
        foreach (var height in new[] { 180, 240, 320 })
            Check(ResourcePolicy.FitDisplayHeight(height, compactWork, 2) == height, "Existing sizes must remain unchanged when they fit.");
        Check(ResourcePolicy.FitDisplayHeight(420, new(0, 0, 10, 10), 3) == 1, "Very small work areas must retain a positive rendered height.");
        Check(ResourcePolicy.TrackingInterval(false).TotalMilliseconds == 1000, "Hidden tracking must be slow.");
        Check(ResourcePolicy.TrackingInterval(true).TotalMilliseconds == 250, "Visible tracking must not run at animation frame rate.");
        Check(HostIdentity.IsCodexPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.2667.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"), "Recognize installed Codex package.");
        Check(!HostIdentity.IsCodexPath(@"C:\Random\ChatGPT.exe"), "Do not attach to unrelated ChatGPT-named programs.");
        Check(!HostIdentity.IsCodexPath(@"C:\Users\x\AppData\Local\OpenAI\Codex\bin\hash\codex.exe"), "Do not treat CLI process as desktop window.");
        Console.WriteLine("PASS bounded decode, work-area display fitting, adaptive tracking, desktop host identity");
    }
}
