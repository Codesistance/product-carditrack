using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Shared.Http;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Everything a relayed log line carries beyond the line itself: the build and handset
/// (<see cref="DescribeDevice"/>, once per batch) and the moment (<see cref="Enrich"/>, per
/// entry — frames, screen, thread, device state, and on a crash the tail of the app's own log).
/// </summary>
/// <remarks>
/// Every field is collected under its own guard and a failure costs that field alone. This runs
/// inside the logger, sometimes on the thread that is about to abort the process, and a
/// description of the phone must never be the reason the crash it describes was lost. Nothing
/// here names the caregiver: no account, no member, no reading — the DPIA's A9 row rests on
/// that, so a new field belongs here only if it is about the software or the device.
/// </remarks>
internal static class MobileDiagnosticsContext
{
    private const string InstallIdKey = "diagnostics.install_id";

    /// <summary>How deep an inner-exception chain is walked for frames.</summary>
    private const int MaxExceptionDepth = 8;

    /// <summary>
    /// What a crash report has to be matched against and the log line does not carry. Module
    /// version id and informational version pin the exact compile; the store build number
    /// (<c>AppVersion</c>) is what the symbols artifact is filed under.
    /// </summary>
    public static MobileDiagnosticsLogRequest DescribeDevice()
    {
        var envelope = new MobileDiagnosticsLogRequest();

        Guard(() =>
        {
            envelope.Platform = ClientIdentity.Platform;
            envelope.AppVersion = ClientIdentity.Version;
        });

        Guard(() =>
        {
            envelope.Manufacturer = DeviceInfo.Current.Manufacturer;
            envelope.Model = DeviceInfo.Current.Model;
            envelope.Device = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
            envelope.OsVersion = DeviceInfo.Current.VersionString;
        });

        Guard(() =>
        {
            envelope.OsDescription = RuntimeInformation.OSDescription;
            envelope.Architecture = RuntimeInformation.ProcessArchitecture.ToString();
            envelope.Runtime = RuntimeInformation.FrameworkDescription;
        });

        Guard(() =>
        {
            envelope.Locale = CultureInfo.CurrentCulture.Name;
            envelope.TimeZone = TimeZoneInfo.Local.Id;
        });

        Guard(() =>
        {
            var assembly = typeof(MobileDiagnosticsContext).Assembly;
            envelope.AppAssemblyVersion =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();
            envelope.ModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N");
        });

        Guard(() =>
        {
            // Random per install, so one phone's repeated crashes group together in Datadog.
            // Names no person and no account; goes with the app's data when it is deleted.
            var installId = Preferences.Default.Get(InstallIdKey, string.Empty);
            if (string.IsNullOrEmpty(installId))
            {
                installId = Guid.NewGuid().ToString("N");
                Preferences.Default.Set(InstallIdKey, installId);
            }

            envelope.InstallId = installId;
        });

        return envelope;
    }

    /// <summary>
    /// Fills the per-moment fields of one entry. <paramref name="isCrash"/> adds the log tail,
    /// which is the one field with a real cost to collect and the one that only matters when
    /// the process is about to end.
    /// </summary>
    public static void Enrich(MobileDiagnosticsLogEntry entry, Exception? exception, bool isCrash)
    {
        if (exception is not null)
        {
            Guard(() =>
            {
                entry.ExceptionType = exception.GetType().FullName;
                entry.ExceptionMessage = exception.Message;
            });
            Guard(() => entry.Frames = Frames(exception));
        }

        Guard(() => entry.Screen = CurrentScreen());
        Guard(() => entry.NetworkAccess = Connectivity.Current.NetworkAccess.ToString());
        Guard(() => entry.UptimeSeconds = Math.Round(AppStartup.Elapsed.TotalSeconds, 1));
        Guard(() =>
        {
            entry.ThreadId = Environment.CurrentManagedThreadId;
            entry.ThreadName = Thread.CurrentThread.Name;
            entry.IsMainThread = MainThread.IsMainThread;
        });
        Guard(() => entry.ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false));
        Guard(() => entry.WorkingSetBytes = Environment.WorkingSet);

        if (isCrash)
            Guard(() => entry.RecentLog = RecentLogTail());
    }

    /// <summary>
    /// The exception chain, outermost first, then each exception's frames from the throw site
    /// down. <c>AggregateException</c> fans out to every inner exception; anything else follows
    /// <c>InnerException</c>. <c>Exception.ToString()</c> already carries all of this as text;
    /// the structured form exists so a frame can be matched to the build's symbols.
    /// </summary>
    private static List<MobileDiagnosticsStackFrame> Frames(Exception exception)
    {
        var frames = new List<MobileDiagnosticsStackFrame>();
        var depth = 0;
        foreach (var current in Chain(exception))
        {
            StackFrame[] walked;
            try
            {
                walked = new StackTrace(current, fNeedFileInfo: true).GetFrames();
            }
            catch (Exception)
            {
                depth++;
                continue;
            }

            for (var index = 0; index < walked.Length; index++)
            {
                if (frames.Count >= MobileDiagnosticsContract.MaxFrames)
                    return frames;

                frames.Add(Describe(walked[index], depth, index));
            }

            depth++;
        }

        return frames;
    }

    private static IEnumerable<Exception> Chain(Exception root)
    {
        var pending = new Queue<(Exception Exception, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();
            yield return current;
            if (depth >= MaxExceptionDepth)
                continue;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Enqueue((inner, depth + 1));
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue((inner, depth + 1));
            }
        }
    }

    private static MobileDiagnosticsStackFrame Describe(StackFrame frame, int depth, int index)
    {
        var described = new MobileDiagnosticsStackFrame { Depth = depth, Index = index };

        Guard(() =>
        {
            var method = frame.GetMethod();
            described.Type = method?.DeclaringType?.FullName;
            described.Method = method?.ToString();
            described.Assembly = method?.Module.Assembly.GetName().Name;
        });
        Guard(() =>
        {
            var il = frame.GetILOffset();
            described.IlOffset = il == StackFrame.OFFSET_UNKNOWN ? null : il;
            var native = frame.GetNativeOffset();
            described.NativeOffset = native == StackFrame.OFFSET_UNKNOWN ? null : native;
        });
        Guard(() =>
        {
            var ip = frame.GetNativeIP();
            described.NativeIp = ip == IntPtr.Zero ? null : $"0x{ip:x}";
            var imageBase = frame.GetNativeImageBase();
            described.NativeImageBase = imageBase == IntPtr.Zero ? null : $"0x{imageBase:x}";
        });
        Guard(() =>
        {
            described.File = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            described.Line = line > 0 ? line : null;
        });

        return described;
    }

    /// <summary>
    /// The Shell route when a Shell is up, otherwise the root page and whatever is pushed or
    /// modal on top of it — enough to say "the sign-in page, with the verify-email page pushed".
    /// </summary>
    private static string? CurrentScreen()
    {
        var root = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (root is null)
            return null;

        if (root is Shell shell)
        {
            var location = shell.CurrentState?.Location?.ToString();
            var modal = shell.Navigation?.ModalStack.LastOrDefault();
            return modal is null ? location : $"{location} + modal {modal.GetType().Name}";
        }

        var top = root is NavigationPage navigation ? navigation.CurrentPage : root;
        var modalTop = root.Navigation?.ModalStack.LastOrDefault();
        var screen = top.GetType().Name;
        return modalTop is null ? screen : $"{screen} + modal {modalTop.GetType().Name}";
    }

    /// <summary>
    /// The newest log file's last <see cref="MobileDiagnosticsContract.MaxRecentLogLength"/>
    /// characters, through a shared-read handle because Serilog holds the file open for
    /// writing. The relay keeps a tail too, so over-reading here only costs bytes.
    /// </summary>
    private static string? RecentLogTail()
    {
        if (!Directory.Exists(AppLogging.LogDirectory))
            return null;

        var newest = Directory.GetFiles(AppLogging.LogDirectory, "*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null)
            return null;

        using var stream = new FileStream(
            newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // UTF-8 is at most four bytes a character; read enough bytes to cover the cap.
        var bytes = (int)Math.Min(stream.Length, MobileDiagnosticsContract.MaxRecentLogLength * 4L);
        stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (text.Length > MobileDiagnosticsContract.MaxRecentLogLength)
            text = text[^MobileDiagnosticsContract.MaxRecentLogLength..];

        // Warning lines can name a member or an API body. A9's claim is diagnostic state
        // only, so the tail that rides a crash is Error/Fatal from the file template
        // ("[ERR]", "[FTL]"), not the Warning+ file as a whole.
        var kept = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("[ERR]", StringComparison.Ordinal)
                           || line.Contains("[FTL]", StringComparison.Ordinal))
            .ToList();
        if (kept.Count == 0)
            return null;

        var filtered = string.Join('\n', kept);
        return filtered.Length <= MobileDiagnosticsContract.MaxRecentLogLength
            ? filtered
            : filtered[^MobileDiagnosticsContract.MaxRecentLogLength..];
    }

    private static void Guard(Action collect)
    {
        try
        {
            collect();
        }
        catch (Exception)
        {
            // One field lost, never the entry — see the class remarks.
        }
    }
}
