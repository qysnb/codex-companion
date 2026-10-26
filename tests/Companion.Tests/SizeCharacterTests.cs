using CodexCompanion.Core;

public static class SizeCharacterTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var characters = new[] { ("elegant", "Astra"), ("mischievous", "Sol"), ("cute", "Luna"), ("fable", "Fable"), ("haiku", "Haiku"), ("pro", "Pro"), ("flash", "Flash") };
        Check(CompanionSettings.Characters.SequenceEqual(characters.Select(x => x.Item1)), "The three persisted character IDs and four new character IDs must remain selectable.");
        var controller = new CompanionController();
        Check(controller.Click() == BubbleKind.Quota, "The first click must still show quota.");
        foreach (var (id, name) in characters)
        {
            Check((new CompanionSettings { Character = id }).Normalize().Character == id, "Known character IDs must survive normalization: " + id);
            Check(CharacterNames.Get(id) == name, "Every selectable character must have its display name: " + id);
            Check(CompanionCommand.Validate("set-character", id) is null, "IPC must accept every selectable character: " + id);
            controller.SetCharacter(id);
        }
        Check(controller.Click() == BubbleKind.Greeting, "Selecting any of the seven characters must preserve click parity.");
        var sizes = new[] { ("small", 180), ("medium", 240), ("large", 320), ("xlarge", 420) };
        Check(CompanionSettings.Sizes.SequenceEqual(sizes), "The menu and settings must share the same ordered size definitions.");
        foreach (var (id, height) in sizes)
        {
            var normalized = (new CompanionSettings { Size = id }).Normalize();
            Check(normalized.Size == id && normalized.Height == height, "Size IDs must preserve the existing heights and support 420 DIP: " + id);
        }
        var defaults = new CompanionSettings();
        Check(defaults.Character == "cute" && defaults.Size == "medium" && defaults.Height == 240, "Luna at medium size must remain the default.");
        var directory = Path.Combine(Path.GetTempPath(), "companion-size-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            foreach (var (id, _) in characters)
            {
                store.Save(new() { Character = id, Size = "xlarge", Language = "ja", RefreshIntervalSeconds = 75, OffsetX = -84, OffsetY = 21, ReducedMotion = true });
                var loaded = store.Load();
                Check(loaded.Character == id && loaded.Size == "xlarge" && loaded.Height == 420 && loaded.Language == "ja" && loaded.RefreshIntervalSeconds == 75 && loaded.OffsetX == -84 && loaded.OffsetY == 21 && loaded.ReducedMotion, "New character/size settings must roundtrip without changing other saved preferences.");
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS seven character selections, persisted IDs, four sizes, defaults and settings roundtrip");
    }
}
