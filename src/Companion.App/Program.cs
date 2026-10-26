using System.Text.Json;
using System.Windows;
using CodexCompanion.Core;
namespace CodexCompanion.App;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
#if DEBUG
            if (args.Length is 2 or 3 && args[0] == "--prepare-art" && (args.Length == 2 || CompanionSettings.Characters.Contains(args[2]))) return Output(AssetInspection.PrepareArt(args[1], args.Length == 3 ? args[2] : null), 0);
#endif
            if (args.SequenceEqual(new[] { "--verify-ui" })) return VerifyUi(false);
            if (args.Length == 3 && args[0] == "--verify-ui" && args[1] == "--preview") return VerifyUi(false, args[2]);
            if (args.SequenceEqual(new[] { "--verify-ui-live" })) return VerifyUi(true);
            if (args.SequenceEqual(new[] { "--check-assets" })) return Output(AssetInspection.Check(), 0);
            if (args.Length is 2 or 3 && args[0] == "--render-preview" && (args.Length == 2 || args[2] == "--quota"))
            {
                _ = new Application(); var preview = new CompanionWindow(); preview.RenderPreview(args[1], args.Length == 3); return 0;
            }
            if (args.SequenceEqual(new[] { "--probe-quota" })) return ProbeAsync().GetAwaiter().GetResult();
            CommandRequest? request = null;
            if (args.Length > 0)
            {
                if (args.Length is not (2 or 4) || args[0] != "--command" || (args.Length == 4 && args[2] != "--value")) return Output(new { ok = false, error = "Usage: --command COMMAND [--value VALUE], or --probe-quota" }, 2);
                request = new(args[1], args.Length == 4 ? args[3] : null);
                if (CompanionCommand.Validate(request.Command, request.Value) is { } error) return Output(new { ok = false, error }, 2);
            }
            using var mutex = new Mutex(true, @"Local\" + CommandPipe.Name, out var first);
            if (!first)
            {
                var result = CommandPipe.SendAsync(request ?? new("show")).GetAwaiter().GetResult();
                if (result is null) return Output(new { ok = false, error = "Companion is starting. Retry shortly." }, 2);
                Write(result); return 0;
            }
            try
            {
                if (request?.Command is "get-status" or "hide" or "exit") return Output(new { ok = false, error = "Companion is not running." }, 2);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var window = new CompanionWindow();
                app.MainWindow = window;
                app.Startup += async (_, _) =>
                {
                    await window.StartAsync();
                    if (request is not null) Output(await window.ExecuteAsync(request), 0);
                };
                app.Exit += (_, _) => window.StopAsync().GetAwaiter().GetResult();
                app.DispatcherUnhandledException += (_, e) =>
                { Output(new { ok = false, error = "Companion encountered a local UI error: " + e.Exception.GetType().Name }, 1); app.Shutdown(1); e.Handled = true; };
                return app.Run();
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e)
        {
#if DEBUG
            return Output(new { ok = false, error = e.GetType().Name, detail = e.Message, stack = e.StackTrace }, 1);
#else
            return Output(new { ok = false, error = "Companion could not start: " + e.GetType().Name }, 1);
#endif
        }
    }
    static int VerifyUi(bool live, string? previewPath = null)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            CompanionWindow? window = null;
            QuotaService? service = null;
            try
            {
                service = live ? new QuotaService() : new QuotaService(_ => Task.FromResult<ICodexConnection>(new PresentationQuota()));
                var warmup = System.Diagnostics.Stopwatch.StartNew();
                QuotaSnapshot snapshot;
                if (live)
                {
                    var ready = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Updated(QuotaSnapshot value)
                    { if (value.Status == QuotaStatus.Live) ready.TrySetResult(value); else if (value.Status == QuotaStatus.NotLoggedIn) ready.TrySetException(new InvalidOperationException("Codex is not signed in.")); }
                    service.Updated += Updated;
                    try { await service.StartAsync(); snapshot = await ready.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
                    finally { service.Updated -= Updated; }
                }
                else snapshot = await service.RefreshAsync(true);
                if (snapshot.Status != QuotaStatus.Live) { await service.DisposeAsync(); throw new InvalidOperationException("Quota warmup did not return live data: " + snapshot.Status); }
                var warmupMs = warmup.Elapsed.TotalMilliseconds;
                window = new CompanionWindow(service); app.MainWindow = window;
                var result = await window.VerifyPresentationAsync(!live, previewPath);
                await window.StopAsync(); window.Close(); Output(new { ok = true, quotaSource = live ? "real official API" : "synthetic test data", backgroundWarmupMilliseconds = warmupMs, result }, 0); app.Shutdown(0);
            }
            catch (Exception error)
            { if (window is not null) await window.StopAsync(); else if (service is not null) await service.DisposeAsync(); Output(new { ok = false, error = error.Message }, 1); app.Shutdown(1); }
        };
        return app.Run();
    }
    static async Task<int> ProbeAsync()
    {
        CodexConnection.CaptureDiagnostics = true;
        await using var quota = new QuotaService();
        var snapshot = await quota.RefreshAsync(true);
        for (var attempt = 0; attempt < 2 && snapshot.Status == QuotaStatus.Loading; attempt++)
            snapshot = await quota.RefreshAsync(true);
        return Output(new { snapshot, connectionDiagnostic = CodexConnection.LastDiagnostic, events = CodexConnection.DiagnosticEvents }, snapshot.Status == QuotaStatus.Live ? 0 : 2);
    }
    static int Output(object value, int exit) { Write(JsonSerializer.Serialize(value, CommandPipe.Json)); return exit; }
    static void Write(string value)
    {
        try { using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; output.WriteLine(value); }
        catch (IOException) { }
    }
}
