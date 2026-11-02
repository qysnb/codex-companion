namespace CodexCompanion.Core;

public static class ApplicationLayout
{
    public static string StartupExecutable(string runtimeDirectory, string processPath)
    {
        var binary = new DirectoryInfo(Path.GetFullPath(runtimeDirectory));
        if (binary.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
            binary.Parent?.Name.Equals("app", StringComparison.OrdinalIgnoreCase) == true && binary.Parent.Parent is { } root)
        {
            var launcher = Path.Combine(root.FullName, "CodexCompanion.exe");
            if (File.Exists(launcher)) return launcher;
        }
        return processPath;
    }
}
