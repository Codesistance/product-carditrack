using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Shared.Http;

namespace CardiTrack.Mobile.Core.Diagnostics;

/// <summary>
/// File-backed implementation of <see cref="IMobileDiagnosticsRelay"/>: one JSON line per entry
/// in a queue file under the app's log directory, drained by <see cref="FlushAsync"/> in batches
/// of <see cref="MobileDiagnosticsContract.MaxEntriesPerBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// MAUI-free on purpose, like the rest of Mobile.Core, so the queue and the send can be tested
/// with a scripted <see cref="HttpMessageHandler"/>. The head supplies the two things only it
/// knows: the key and API address stamped into the build, and an envelope describing the
/// handset (<see cref="MobileDiagnosticsLogRequest"/> minus its entries), asked for at send
/// time rather than at construction because <c>DeviceInfo</c> is not worth trusting during
/// startup and a crash must never be lost to a description of the phone failing.
/// </para>
/// <para>
/// Two refusals end retrying for the process: 401 (the build's key is not this environment's)
/// and 404 (the environment has no relay). Both are configuration, and neither improves by
/// asking again, so the queue is dropped rather than growing until the cap on every launch. A
/// 400 drops the offending batch alone — one malformed line must not hold the rest hostage —
/// while transport failures and 5xx leave everything in place for next time.
/// </para>
/// </remarks>
public sealed class MobileDiagnosticsRelay : IMobileDiagnosticsRelay
{
    /// <summary>
    /// Oldest lines are dropped past this. Error+ only, so a healthy phone never approaches it;
    /// a phone crashing at every launch with no network reaches it in a couple of hundred
    /// launches, by which point the first hundred crashes have said everything.
    /// </summary>
    public const int MaxQueuedEntries = 200;

    private const int MaxBatchesPerFlush = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly string _queuePath;
    private readonly Func<MobileDiagnosticsLogRequest> _envelope;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private volatile bool _refused;

    public MobileDiagnosticsRelay(
        HttpClient http, string? key, string queuePath, Func<MobileDiagnosticsLogRequest> envelope)
    {
        _http = http;
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        _queuePath = queuePath;
        _envelope = envelope;
    }

    public bool Enabled => _key is not null && _http.BaseAddress is not null;

    public void Record(MobileDiagnosticsLogEntry entry)
    {
        if (!Enabled)
            return;

        try
        {
            var line = JsonSerializer.Serialize(Bounded(entry), Json);
            lock (_gate)
            {
                var lines = ReadQueueLocked();
                lines.Add(line);
                if (lines.Count > MaxQueuedEntries)
                    lines.RemoveRange(0, lines.Count - MaxQueuedEntries);
                WriteQueueLocked(lines);
            }
        }
        catch (Exception)
        {
            // Called from a Serilog sink: anything thrown here would be logged, which would
            // call here again. The relay is best-effort by definition; the file sink still has
            // the line.
        }
    }

    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (!Enabled || _refused)
            return false;

        if (!await _flushing.WaitAsync(0, ct).ConfigureAwait(false))
            return false;

        try
        {
            for (var batch = 0; batch < MaxBatchesPerFlush; batch++)
            {
                List<string> queued;
                lock (_gate)
                    queued = ReadQueueLocked();
                if (queued.Count == 0)
                    return true;

                var take = queued.Take(MobileDiagnosticsContract.MaxEntriesPerBatch).ToList();
                var entries = take
                    .Select(Parse)
                    .Where(e => e is not null)
                    .Cast<MobileDiagnosticsLogEntry>()
                    .ToList();

                if (entries.Count > 0)
                {
                    var request = Bounded(_envelope());
                    request.Entries = entries;

                    using var message = new HttpRequestMessage(HttpMethod.Post, MobileDiagnosticsContract.LogsPath)
                    {
                        Content = JsonContent.Create(request, options: Json),
                    };
                    message.Headers.TryAddWithoutValidation(MobileDiagnosticsContract.KeyHeader, _key);

                    using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                    {
                        _refused = true;
                        lock (_gate)
                            WriteQueueLocked([]);
                        return false;
                    }

                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
                        return false;
                }

                // Sent (or unparseable, or refused as malformed): off the queue either way.
                lock (_gate)
                {
                    var remaining = ReadQueueLocked();
                    remaining.RemoveRange(0, Math.Min(take.Count, remaining.Count));
                    WriteQueueLocked(remaining);
                }
            }

            return false;
        }
        catch (Exception)
        {
            // Offline, DNS, timeout, a half-written queue file: the entries stay for next time.
            return false;
        }
        finally
        {
            _flushing.Release();
        }
    }

    public bool TryFlushBlocking(TimeSpan budget)
    {
        if (!Enabled)
            return false;

        try
        {
            using var cts = new CancellationTokenSource(budget);
            // Task.Run so the HTTP continuation never needs the calling thread back: this is
            // called on the UI thread from the unhandled-exception handler, and awaiting there
            // while blocking here would deadlock until the budget ran out.
            var flush = Task.Run(() => FlushAsync(cts.Token), CancellationToken.None);
            return flush.Wait(budget) && flush.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private List<string> ReadQueueLocked() =>
        File.Exists(_queuePath)
            ? File.ReadAllLines(_queuePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
            : [];

    private void WriteQueueLocked(List<string> lines)
    {
        if (lines.Count == 0)
        {
            if (File.Exists(_queuePath))
                File.Delete(_queuePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_queuePath)!);
        File.WriteAllLines(_queuePath, lines);
    }

    private static MobileDiagnosticsLogEntry? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<MobileDiagnosticsLogEntry>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The API refuses anything over the contract's ceilings; cut here so it never has to. Text
    /// is cut from the end except the recent-log tail, whose newest lines are its last ones.
    /// </summary>
    internal static MobileDiagnosticsLogEntry Bounded(MobileDiagnosticsLogEntry entry) => new()
    {
        Timestamp = entry.Timestamp,
        Level = entry.Level.Trim().ToLowerInvariant() switch
        {
            "fatal" or "critical" => "Fatal",
            "warning" => "Warning",
            _ => "Error",
        },
        Message = Head(entry.Message, MobileDiagnosticsContract.MaxMessageLength) ?? string.Empty,
        Exception = Head(entry.Exception, MobileDiagnosticsContract.MaxExceptionLength),
        ExceptionType = Head(entry.ExceptionType, MobileDiagnosticsContract.MaxFrameFieldLength),
        ExceptionMessage = Head(entry.ExceptionMessage, MobileDiagnosticsContract.MaxMessageLength),
        Frames = entry.Frames
            .Take(MobileDiagnosticsContract.MaxFrames)
            .Select(Bounded)
            .ToList(),
        Source = Head(entry.Source, MobileDiagnosticsContract.MaxFieldLength),
        Screen = Head(entry.Screen, MobileDiagnosticsContract.MaxFieldLength),
        NetworkAccess = Head(entry.NetworkAccess, MobileDiagnosticsContract.MaxFieldLength),
        UptimeSeconds = entry.UptimeSeconds,
        ThreadId = entry.ThreadId,
        ThreadName = Head(entry.ThreadName, MobileDiagnosticsContract.MaxFieldLength),
        IsMainThread = entry.IsMainThread,
        ManagedMemoryBytes = entry.ManagedMemoryBytes,
        WorkingSetBytes = entry.WorkingSetBytes,
        RecentLog = Tail(entry.RecentLog, MobileDiagnosticsContract.MaxRecentLogLength),
    };

    private static MobileDiagnosticsStackFrame Bounded(MobileDiagnosticsStackFrame frame) => new()
    {
        Depth = frame.Depth,
        Index = frame.Index,
        Type = Head(frame.Type, MobileDiagnosticsContract.MaxFrameFieldLength),
        Method = Head(frame.Method, MobileDiagnosticsContract.MaxFrameFieldLength),
        Assembly = Head(frame.Assembly, MobileDiagnosticsContract.MaxFrameFieldLength),
        IlOffset = frame.IlOffset,
        NativeOffset = frame.NativeOffset,
        NativeIp = Head(frame.NativeIp, MobileDiagnosticsContract.MaxFieldLength),
        NativeImageBase = Head(frame.NativeImageBase, MobileDiagnosticsContract.MaxFieldLength),
        File = Head(frame.File, MobileDiagnosticsContract.MaxFrameFieldLength),
        Line = frame.Line,
    };

    private static MobileDiagnosticsLogRequest Bounded(MobileDiagnosticsLogRequest envelope) => new()
    {
        Platform = Head(envelope.Platform, MobileDiagnosticsContract.MaxFieldLength),
        AppVersion = Head(envelope.AppVersion, MobileDiagnosticsContract.MaxFieldLength),
        Device = Head(envelope.Device, MobileDiagnosticsContract.MaxFieldLength),
        Manufacturer = Head(envelope.Manufacturer, MobileDiagnosticsContract.MaxFieldLength),
        Model = Head(envelope.Model, MobileDiagnosticsContract.MaxFieldLength),
        OsVersion = Head(envelope.OsVersion, MobileDiagnosticsContract.MaxFieldLength),
        OsDescription = Head(envelope.OsDescription, MobileDiagnosticsContract.MaxFieldLength),
        Architecture = Head(envelope.Architecture, MobileDiagnosticsContract.MaxFieldLength),
        Runtime = Head(envelope.Runtime, MobileDiagnosticsContract.MaxFieldLength),
        Locale = Head(envelope.Locale, MobileDiagnosticsContract.MaxFieldLength),
        TimeZone = Head(envelope.TimeZone, MobileDiagnosticsContract.MaxFieldLength),
        AppAssemblyVersion = Head(envelope.AppAssemblyVersion, MobileDiagnosticsContract.MaxFieldLength),
        ModuleVersionId = Head(envelope.ModuleVersionId, MobileDiagnosticsContract.MaxFieldLength),
        InstallId = Head(envelope.InstallId, MobileDiagnosticsContract.MaxFieldLength),
    };

    private static string? Head(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static string? Tail(string? value, int max) =>
        value is null || value.Length <= max ? value : value[^max..];
}
