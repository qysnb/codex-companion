using System.Collections.Concurrent;
namespace CodexCompanion.Core;

public sealed record LocalQuotaMonitorStats(string State, int TrackedFiles, int RetainedBufferBytes, long BytesRead, long Readings, DateTimeOffset? LastReceivedAt, double? LastEventLagMs = null);
public sealed class LocalQuotaMonitor : IAsyncDisposable
{
    public const int MaxTrackedFiles = 16;
    static long generationCounter = DateTimeOffset.UtcNow.Ticks;
    readonly string home, sessions;
    readonly ConcurrentDictionary<string, bool> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Cursor> files = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim wake = new(0, 1);
    readonly byte[] readBuffer = new byte[8192];
    readonly object lifecycle = new();
    readonly object authenticationGate = new();
    readonly object pendingGate = new();
    Task? loop;
    FileSystemWatcher? watcher, authWatcher;
    long generation, validAfter, bytesRead, readings;
    int rescan, reopen;
    bool disposed, attached;
    QuotaSnapshot? last;
    DateTimeOffset? receivedAt;
    double? eventLagMs;
    LocalQuotaMonitorStats stats = new("starting", 0, 0, 0, 0, null);
    sealed class Cursor(long offset, bool boundary)
    {
        internal long Offset = offset;
        internal long Touched = Environment.TickCount64;
        internal readonly LocalQuotaDecoder Decoder = Create(boundary);
        static LocalQuotaDecoder Create(bool boundary) { var decoder = new LocalQuotaDecoder(); decoder.Reset(!boundary); return decoder; }
    }
    public LocalQuotaMonitor(string home)
    {
        this.home = Path.GetFullPath(home);
        sessions = Path.Combine(this.home, "sessions");
        generation = Interlocked.Increment(ref generationCounter);
    }
    public event Action<LocalQuotaReading>? Reading;
    public event Action<long, bool>? Invalidated;
    public LocalQuotaMonitorStats Stats => Volatile.Read(ref stats);
    public long Generation => Volatile.Read(ref generation);
    public Task StartAsync()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loop is null) { validAfter = DateTimeOffset.UtcNow.UtcTicks; loop = Task.Run(RunAsync); }
        }
        return Task.CompletedTask;
    }
    void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    void Queue(string path, bool created)
    {
        if (stop.IsCancellationRequested || !SafePath(path)) return;
        lock (pendingGate)
        {
            if (pending.Count >= 64 && !pending.ContainsKey(path)) { Interlocked.Exchange(ref rescan, 1); Wake(); return; }
            pending.AddOrUpdate(path, created, (_, previous) => previous || created);
        }
        Wake();
    }
    bool SafePath(string path)
    {
        if (!path.StartsWith(sessions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("rollout-", StringComparison.Ordinal) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            for (var p = path; p.Length >= sessions.Length; p = Path.GetDirectoryName(p)!)
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    void AuthChanged()
    {
        lock (authenticationGate)
        {
            if (stop.IsCancellationRequested) return;
            Volatile.Write(ref validAfter, DateTimeOffset.UtcNow.UtcTicks);
            var epoch = Interlocked.Increment(ref generationCounter);
            Volatile.Write(ref generation, epoch);
            foreach (var callback in Invalidated?.GetInvocationList() ?? [])
                try { ((Action<long, bool>)callback)(epoch, true); } catch { }
        }
        Wake();
    }
    void Track(string path, long offset)
    {
        if (files.Count >= MaxTrackedFiles && !files.ContainsKey(path)) files.Remove(files.MinBy(item => item.Value.Touched).Key);
        var boundary = offset == 0;
        if (!boundary)
        {
            using var stream = Open(path); stream.Position = offset - 1; boundary = stream.ReadByte() == '\n';
        }
        files[path] = new(offset, boundary);
    }
    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    void Baseline(bool recovery = false)
    {
        var recent = new PriorityQueue<(string Path, long Length), DateTime>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(sessions, "rollout-*.jsonl", options))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) || !SafePath(path)) continue;
                recent.Enqueue((path, info.Length), info.LastWriteTimeUtc);
                if (recent.Count > MaxTrackedFiles) recent.Dequeue();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        while (recent.TryDequeue(out var item, out _))
            try { if (!files.ContainsKey(item.Path)) Track(item.Path, recovery ? Math.Max(0, item.Length - 65536) : item.Length); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    bool Setup()
    {
        if (!Directory.Exists(sessions)) return false;
        Baseline(attached);
        watcher = new FileSystemWatcher(sessions, "rollout-*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, InternalBufferSize = 8192 };
        watcher.Changed += (_, e) => Queue(e.FullPath, false);
        watcher.Created += (_, e) => Queue(e.FullPath, true);
        watcher.Renamed += (_, e) => Queue(e.FullPath, true);
        watcher.Error += (_, _) => { Interlocked.Exchange(ref reopen, 1); Wake(); };
        authWatcher = new FileSystemWatcher(home, "auth.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        authWatcher.Changed += (_, _) => AuthChanged(); authWatcher.Created += (_, _) => AuthChanged();
        authWatcher.Deleted += (_, _) => AuthChanged(); authWatcher.Renamed += (_, _) => AuthChanged();
        authWatcher.EnableRaisingEvents = true; watcher.EnableRaisingEvents = true;
        attached = true;
        return true;
    }
    void UpdateStats(string state) => Volatile.Write(ref stats, new(state, files.Count, files.Count * 8192 + readBuffer.Length, bytesRead, readings, receivedAt, eventLagMs));
    async Task RunAsync()
    {
        var observedGeneration = generation;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (Interlocked.Exchange(ref reopen, 0) != 0)
                    { watcher?.Dispose(); authWatcher?.Dispose(); watcher = authWatcher = null; }
                    if (watcher is null && !Setup()) { UpdateStats("unavailable"); await wake.WaitAsync(TimeSpan.FromSeconds(5), stop.Token); continue; }
                    if (observedGeneration != Volatile.Read(ref generation)) { observedGeneration = generation; last = null; receivedAt = null; }
                    // Metadata recovery covers missed notifications for tracked files.
                    foreach (var path in files.Keys.ToArray())
                        lock (pendingGate) { if (pending.Count < 64 && !pending.ContainsKey(path)) pending.TryAdd(path, false); }
                    if (Interlocked.Exchange(ref rescan, 0) != 0)
                    {
                        // Recovery examines a bounded tail. Timestamp and generation
                        // gates still reject quota events from before attachment.
                        Baseline(true);
                    }
                    foreach (var item in pending.ToArray())
                        if (pending.TryRemove(item.Key, out var created)) await DrainAsync(item.Key, created);
                    UpdateStats("listening");
                    await wake.WaitAsync(TimeSpan.FromSeconds(1), stop.Token);
                    await Task.Delay(35, stop.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { watcher?.Dispose(); authWatcher?.Dispose(); watcher = authWatcher = null; UpdateStats("unavailable"); await Task.Delay(1000, stop.Token); }
            }
        }
        catch (OperationCanceledException) { }
        finally { watcher?.Dispose(); authWatcher?.Dispose(); files.Clear(); pending.Clear(); UpdateStats("stopped"); }
    }
    async Task DrainAsync(string path, bool created)
    {
        try
        {
            if (!SafePath(path)) return;
            if (!created && files.TryGetValue(path, out var existing) && new FileInfo(path).Length == existing.Offset) return;
            using var stream = Open(path);
            if (!files.TryGetValue(path, out var cursor))
            {
                Track(path, created ? 0 : Math.Max(0, stream.Length - 65536)); cursor = files[path];
            }
            else if (created || stream.Length < cursor.Offset) { Track(path, 0); cursor = files[path]; }
            cursor.Touched = Environment.TickCount64; stream.Position = cursor.Offset;
            var budget = 1024 * 1024;
            while (budget > 0 && !stop.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, budget)), stop.Token);
                if (count == 0) break;
                var epoch = Volatile.Read(ref generation);
                cursor.Decoder.Feed(readBuffer.AsSpan(0, count), snapshot => Offer(snapshot, epoch));
                cursor.Offset += count; bytesRead += count; budget -= count;
            }
            if (stream.Length > cursor.Offset) Queue(path, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { files.Remove(path); }
    }
    void Offer(QuotaSnapshot snapshot, long epoch)
    {
        var now = DateTimeOffset.UtcNow;
        if (stop.IsCancellationRequested || epoch != Volatile.Read(ref generation) || snapshot.UpdatedAt is not { } time ||
            time.UtcTicks < Volatile.Read(ref validAfter) || time > now.AddSeconds(2) ||
            last?.UpdatedAt > time || last == snapshot) return;
        last = snapshot; receivedAt = now; eventLagMs = Math.Round(Math.Max(0, (now - time).TotalMilliseconds), 1); readings++;
        foreach (var callback in Reading?.GetInvocationList() ?? [])
            try { ((Action<LocalQuotaReading>)callback)(new(snapshot, epoch, now)); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (lifecycle) { if (disposed) return; disposed = true; running = loop; }
        stop.Cancel(); Wake();
        if (running is not null) await running.ConfigureAwait(false);
        stop.Dispose(); wake.Dispose();
    }
}
