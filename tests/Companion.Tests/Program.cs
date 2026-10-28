using CodexCompanion.Core;

try
{
if (args.Contains("--verify-failure-exit")) throw new InvalidOperationException("Controlled test failure: expected exit code 1.");
var passed = 0;
void Check(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
Check("ten clicks alternate quota and greeting", () => {
    var c = new CompanionController();
    for (var i = 0; i < 10; i++) Assert(c.Click() == (i % 2 == 0 ? BubbleKind.Quota : BubbleKind.Greeting));
});
Check("selection preserves click sequence and invalid selections rejected", () => {
    var c = new CompanionController(); c.Click(); c.SetCharacter("elegant"); c.SetLanguage("ja");
    Assert(c.Click() == BubbleKind.Greeting && c.Character == "elegant" && c.Language == "ja");
    try { c.SetCharacter("unknown"); throw new Exception("Accepted invalid character"); } catch (ArgumentException) { }
});
Check("newer interaction invalidates old asynchronous response", () => {
    var c = new CompanionController(); c.Click(); var version=c.Version; c.Click(); Assert(!c.IsCurrent(version));
    c.CloseBubble(); Assert(c.Bubble == BubbleKind.None);
});
Check("shuffle covers all lines and never repeats at cycle boundary", () => {
    var bag = new ShuffleBag(new[] { "a", "b", "c" }, new Random(7));
    string? previous = null;
    for (var i = 0; i < 20; i++) { var round = new HashSet<string>(); for (var j=0;j<3;j++) { var next=bag.Next(); Assert(next!=previous); round.Add(next); previous=next; } Assert(round.Count==3); }
});
Check("settings normalize invalid persisted values", () => {
    var s = new CompanionSettings { Character="bad", Language="bad", Size="bad", OffsetX=double.NaN, OffsetY=1e9 }.Normalize();
    Assert(s.Character=="cute" && s.Language=="zh-CN" && s.Height==240 && s.OffsetX==0 && s.OffsetY==0);
});
Check("settings roundtrip and corrupt file recovery", () => {
    var dir=Path.Combine(Path.GetTempPath(),"companion-tests-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
    try { var store=new SettingsStore(Path.Combine(dir,"settings.json")); store.Save(new() { Language="ja", Size="large" }); Assert(store.Load().Height==320 && store.Load().Language=="ja"); File.WriteAllText(store.Path,"{"); Assert(store.Load().Character=="cute"); }
    finally { Directory.Delete(dir,true); }
});
Check("theme rejects non-hex and transparent input and persists independent colors", () => {
    foreach (var invalid in new string?[] { null, "Red", "#fff", "#00334455", "#GG3344", "<script>" })
    {
        Assert(!CompanionSettings.IsThemeColor(invalid));
        var normalized = new CompanionSettings { QuotaTextColor = invalid!, QuotaBarColor = invalid! }.Normalize();
        Assert(normalized.QuotaTextColor == "#3D2F42" && normalized.QuotaBarColor == "#00A9D6");
    }
    var dir = Path.Combine(Path.GetTempPath(), "companion-theme-" + Guid.NewGuid());
    try
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        store.Save(new() { QuotaTextColor = "#aabbcc", QuotaBarColor = "#001122", Character = "flash", RefreshIntervalSeconds = 60 });
        var saved = store.Load();
        Assert(saved.QuotaTextColor == "#AABBCC" && saved.QuotaBarColor == "#001122" && saved.Character == "flash" && saved.RefreshIntervalSeconds == 60);
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
Console.WriteLine($"{passed} interaction/settings tests passed.");
SizeCharacterTests.Run();
RuntimeTests.Run();
ResourceTests.Run();
PresentationTests.Run();
QuotaTests.Run();
QuotaRecoveryTests.Run();
QuotaResetTests.Run();
QuotaDeadlineTests.Run();
return 0;
}
catch (Exception error)
{
    // A failed test is a normal nonzero result, not an unhandled application crash.
    var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
    if (message.Length > 300) message = message[..300] + "…";
    Console.Error.WriteLine($"FAIL {error.GetType().Name}: {message}");
    return 1;
}
