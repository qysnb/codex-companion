using CodexCompanion.Core;

public static class BubbleDurationTests
{
    public static void Run()
    {
        var defaults = new CompanionSettings();
        Require(defaults.BubbleDurationSeconds == -1 &&
            defaults.GetBubbleDuration(BubbleKind.Quota) == TimeSpan.FromSeconds(12) &&
            defaults.GetBubbleDuration(BubbleKind.Greeting) == TimeSpan.FromSeconds(7),
            "Existing quota/greeting defaults must remain 12/7 seconds.");

        foreach (var seconds in new[] { 1, 3, 5, 10, 23, 86400 })
        {
            var settings = new CompanionSettings { BubbleDurationSeconds = seconds }.Normalize();
            Require(settings.BubbleDurationSeconds == seconds &&
                settings.GetBubbleDuration(BubbleKind.Quota) == TimeSpan.FromSeconds(seconds) &&
                settings.GetBubbleDuration(BubbleKind.Greeting) == TimeSpan.FromSeconds(seconds),
                "A finite preset or custom duration must apply to both bubble kinds.");
        }
        var permanent = new CompanionSettings { BubbleDurationSeconds = 0 }.Normalize();
        Require(permanent.BubbleDurationSeconds == 0 &&
            permanent.GetBubbleDuration(BubbleKind.Quota) is null && permanent.GetBubbleDuration(BubbleKind.Greeting) is null,
            "Permanent mode must not create a finite timeout.");
        foreach (var invalid in new[] { int.MinValue, -2, 86401, int.MaxValue })
            Require(new CompanionSettings { BubbleDurationSeconds = invalid }.Normalize().BubbleDurationSeconds == -1,
                "An invalid persisted duration must fall back to the original defaults.");

        var lifetime = new BubbleLifetime();
        lifetime.OpenPermanent();
        var remaining = lifetime.Remaining;
        for (var i = 0; i < 10; i++) lifetime.Advance(TimeSpan.MaxValue, i % 2 == 0);
        Require(lifetime.IsOpen && lifetime.IsPermanent && lifetime.Remaining == remaining,
            "Permanent bubbles must not count down, even after very large elapsed intervals.");
        lifetime.Close();
        Require(!lifetime.IsOpen && !lifetime.IsPermanent && lifetime.Remaining == TimeSpan.Zero,
            "Manual close must end permanent mode and reset the lifetime.");

        lifetime.OpenPermanent();
        lifetime.Open(TimeSpan.FromSeconds(3));
        Require(lifetime.IsOpen && !lifetime.IsPermanent,
            "Opening a finite bubble must replace a previous permanent lifetime.");
        lifetime.Advance(TimeSpan.FromHours(1), true);
        Require(lifetime.Remaining == TimeSpan.FromSeconds(3), "Hover must pause a configured duration.");
        lifetime.Advance(TimeSpan.FromSeconds(-1), false);
        Require(lifetime.Remaining == TimeSpan.FromSeconds(3), "Negative elapsed time must not extend the bubble.");
        lifetime.Advance(TimeSpan.FromMilliseconds(2999), false);
        Require(lifetime.IsOpen && lifetime.Remaining == TimeSpan.FromMilliseconds(1), "Finite bubbles must not close early.");
        lifetime.Advance(TimeSpan.FromMilliseconds(1), false);
        Require(!lifetime.IsOpen && lifetime.Remaining == TimeSpan.Zero, "Finite bubbles must expire exactly at the selected duration.");
        lifetime.Open(TimeSpan.FromSeconds(1));
        lifetime.Advance(TimeSpan.MaxValue, false);
        Require(!lifetime.IsOpen && lifetime.Remaining == TimeSpan.Zero, "Delayed ticks must clamp expiry to zero.");
        lifetime.Open(TimeSpan.FromSeconds(10));
        lifetime.Advance(TimeSpan.FromSeconds(9), false);
        lifetime.Open(TimeSpan.FromSeconds(5));
        Require(lifetime.Remaining == TimeSpan.FromSeconds(5), "A new click must restart the selected duration.");

        var directory = Path.Combine(Path.GetTempPath(), "companion-duration-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            foreach (var seconds in new[] { -1, 0, 1, 23, 86400 })
            {
                store.Save(new() { BubbleDurationSeconds = seconds, Character = "flash", Language = "ja", Size = "xlarge", RefreshIntervalSeconds = 60 });
                var restored = store.Load();
                Require(restored.BubbleDurationSeconds == seconds && restored.Character == "flash" && restored.Language == "ja" &&
                    restored.Size == "xlarge" && restored.RefreshIntervalSeconds == 60,
                    "Duration persistence must preserve its value and unrelated preferences.");
            }
            File.WriteAllText(path, "{\"Character\":\"flash\",\"Language\":\"en\"}");
            var legacy = store.Load();
            Require(legacy.BubbleDurationSeconds == -1 && legacy.Character == "flash" && legacy.Language == "en",
                "Older settings files without duration must retain the old defaults and preferences.");
            File.WriteAllText(path, "{\"BubbleDurationSeconds\":86401}");
            Require(store.Load().BubbleDurationSeconds == -1, "Loading out-of-range duration must recover safely.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        Console.WriteLine("PASS bubble duration defaults, presets/custom bounds, permanent/manual close, hover/exact expiry, replacement and persistence migration");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
