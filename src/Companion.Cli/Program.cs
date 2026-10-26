using System.Diagnostics;
using System.Text.Json;
using CodexCompanion.App;
using CodexCompanion.Core;

int Error(string message, int code = 2) { Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message })); return code; }
try
{
    if (args.Length is not (2 or 4) || args[0] != "--command" || (args.Length == 4 && args[2] != "--value"))
        return Error("Usage: --command COMMAND [--value VALUE]");
    var command = new CommandRequest(args[1], args.Length == 4 ? args[3] : null);
    if (CompanionCommand.Validate(command.Command, command.Value) is { } error) return Error(error);
    var reply = await CommandPipe.SendAsync(command);
    if (reply is null)
    {
        if (command.Command is "get-status" or "hide" or "exit") return Error("Companion is not running.");
        var executable = Path.Combine(AppContext.BaseDirectory, "CodexCompanion.exe");
        if (!File.Exists(executable)) return Error("The complete companion package is required.");
        // Open a fixed, adjacent GUI executable through the Windows application
        // launcher, independently of the console client's captured pipe handles.
        // No user argument, command string, URL, or script is passed to the shell.
        using var process = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory });
        for (var attempt = 0; attempt < 6 && reply is null; attempt++)
        { await Task.Delay(200); reply = await CommandPipe.SendAsync(command); }
    }
    if (reply is null) return Error("Companion is starting. Retry shortly.");
    Console.WriteLine(reply); return 0;
}
catch (Exception error)
{ return Error("Local companion command unavailable: " + error.GetType().Name, 1); }
