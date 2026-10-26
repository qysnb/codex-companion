using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexCompanion.Core;
namespace CodexCompanion.App;
internal record CommandRequest(string Command, string? Value = null);
internal sealed class CommandPipe : IAsyncDisposable
{
    internal static readonly string Name = "CodexCompanion-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..24];
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    readonly CancellationTokenSource stop = new();
    readonly Func<CommandRequest, Task<object>> handler;
    readonly Task loop;
    internal CommandPipe(Func<CommandRequest, Task<object>> handler) { this.handler = handler; loop = Task.Run(RunAsync); }
    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                var line = await ReadLineAsync(reader, timeout.Token);
                object result;
                try
                {
                    var request = JsonSerializer.Deserialize<CommandRequest>(line, Json) ?? throw new JsonException();
                    var error = CompanionCommand.Validate(request.Command, request.Value);
                    result = error is null ? await handler(request).WaitAsync(timeout.Token) : new { ok = false, error };
                }
                catch (JsonException) { result = new { ok = false, error = "Invalid command payload." }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, Json).AsMemory(), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            { if (!stop.IsCancellationRequested) try { await Task.Delay(250, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } }
        }
    }
    static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder(); var one = new char[1];
        while (line.Length < 4096)
        { if (await reader.ReadAsync(one.AsMemory(), token) == 0 || one[0] == '\n') return line.ToString(); if (one[0] != '\r') line.Append(one[0]); }
        throw new IOException("Command too long.");
    }
    internal static async Task<string?> SendAsync(CommandRequest command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { using var connect = new CancellationTokenSource(700); await pipe.ConnectAsync(connect.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { return null; }
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(command, Json).AsMemory(), timeout.Token);
        return await ReadLineAsync(reader, timeout.Token);
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await loop.ConfigureAwait(false); stop.Dispose(); }
}
