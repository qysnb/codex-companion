using CodexCompanion.Core;

public static class LayoutTests
{
    public static void Run()
    {
        var testBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "companion-layout-" + Guid.NewGuid()));
        var root = Path.Combine(testBase, "package with spaces");
        void CheckPath(string path) { if (!Path.GetFullPath(path).StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Test path escaped its temporary directory."); }
        var binary = Path.Combine(root, "app", "bin"); Directory.CreateDirectory(binary);
        try
        {
            var actual = Path.Combine(binary, "CodexCompanion.exe");
            var launcher = Path.Combine(root, "CodexCompanion.exe");
            if (ApplicationLayout.StartupExecutable(binary, actual) != actual) throw new Exception("Missing root launcher must preserve the runtime executable.");
            File.WriteAllText(launcher, "test fixture");
            if (ApplicationLayout.StartupExecutable(binary, actual) != launcher) throw new Exception("Portable layout must use the adjacent root launcher for startup.");
            var old = Path.Combine(root, "plugins", "codex-companion", "bin"); Directory.CreateDirectory(old);
            var cachedExe = Path.Combine(old, "CodexCompanion.exe");
            if (ApplicationLayout.StartupExecutable(old, cachedExe) != cachedExe) throw new Exception("Legacy or cached plugins must keep their own executable.");
            var relocated = root + " relocated";
            CheckPath(root); CheckPath(relocated);
            Directory.Move(root, relocated);
            root = relocated;
            if (ApplicationLayout.StartupExecutable(Path.Combine(root, "app", "bin"), Path.Combine(root, "app", "bin", "CodexCompanion.exe")) != Path.Combine(root, "CodexCompanion.exe"))
                throw new Exception("Relocation must not retain the old package path.");
            Console.WriteLine("PASS portable English layout, spaces, relocation, missing launcher and cached-plugin startup fallback");
        }
        finally { CheckPath(root); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
