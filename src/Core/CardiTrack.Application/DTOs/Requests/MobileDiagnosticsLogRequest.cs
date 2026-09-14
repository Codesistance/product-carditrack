namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// One batch of the mobile app's own Error-and-above log lines, relayed so they reach Datadog
/// (see <c>CardiTrack.Shared.Http.MobileDiagnosticsContract</c>). The envelope describes the
/// build and handset once; each entry is one Serilog event as the phone rendered it.
/// </summary>
/// <remarks>
/// No user identity by design: the endpoint is anonymous because the crash it exists for
/// happens on the sign-in screen, before there is a session to speak of. <see cref="InstallId"/>
/// is a random per-install value so one phone's repeated crashes group together; it names no
/// person and is cleared with the app's data.
/// </remarks>
public class MobileDiagnosticsLogRequest
{
    /// <summary>"android" or "ios", as <c>X-Client-Platform</c> spells it.</summary>
    public string? Platform { get; set; }

    /// <summary>"0.2.304+1512", as <c>X-Client-Version</c> spells it.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Manufacturer and model, e.g. "Apple iPhone17,1".</summary>
    public string? Device { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public string? OsVersion { get; set; }

    /// <summary><c>RuntimeInformation.OSDescription</c>.</summary>
    public string? OsDescription { get; set; }

    /// <summary><c>RuntimeInformation.ProcessArchitecture</c>, e.g. "Arm64".</summary>
    public string? Architecture { get; set; }

    /// <summary><c>RuntimeInformation.FrameworkDescription</c>, e.g. ".NET 10.0.1".</summary>
    public string? Runtime { get; set; }

    public string? Locale { get; set; }

    public string? TimeZone { get; set; }

    /// <summary>The app assembly's informational version — what the build stamped, if anything.</summary>
    public string? AppAssemblyVersion { get; set; }

    /// <summary>
    /// The app assembly's module version id: changes with every compile, so it pins a report
    /// to one exact build alongside the store build number.
    /// </summary>
    public string? ModuleVersionId { get; set; }

    public string? InstallId { get; set; }

    public List<MobileDiagnosticsLogEntry> Entries { get; set; } = [];
}

public class MobileDiagnosticsLogEntry
{
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>"Warning", "Error" or "Fatal" — the Serilog level names the app writes.</summary>
    public string Level { get; set; } = "Error";

    /// <summary>The rendered message, with its properties already substituted in.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// <c>Exception.ToString()</c> — type, message, stack and the whole inner chain, truncated
    /// on the phone only past <c>MobileDiagnosticsContract.MaxExceptionLength</c>.
    /// </summary>
    public string? Exception { get; set; }

    /// <summary>The outermost exception's full type name.</summary>
    public string? ExceptionType { get; set; }

    /// <summary>The outermost exception's message.</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>
    /// Every frame the runtime could walk, across the exception chain (see
    /// <see cref="MobileDiagnosticsStackFrame.Depth"/>). Structured so a frame can be matched
    /// to a symbol file: on an AOT build the native address is what the dSYM resolves.
    /// </summary>
    public List<MobileDiagnosticsStackFrame> Frames { get; set; } = [];

    /// <summary>The Serilog <c>SourceContext</c>, i.e. which class logged it.</summary>
    public string? Source { get; set; }

    /// <summary>The Shell route or root page on screen when the line was written.</summary>
    public string? Screen { get; set; }

    /// <summary>MAUI <c>Connectivity.NetworkAccess</c> at the time, e.g. "Internet", "None".</summary>
    public string? NetworkAccess { get; set; }

    /// <summary>Seconds since the app's managed entry point ran.</summary>
    public double? UptimeSeconds { get; set; }

    public int? ThreadId { get; set; }

    public string? ThreadName { get; set; }

    public bool? IsMainThread { get; set; }

    /// <summary><c>GC.GetTotalMemory(false)</c>.</summary>
    public long? ManagedMemoryBytes { get; set; }

    /// <summary><c>Environment.WorkingSet</c>, where the platform reports it.</summary>
    public long? WorkingSetBytes { get; set; }

    /// <summary>
    /// The tail of the device's own Warning+ log file, carried on crash entries only — what
    /// the app had already complained about before it died.
    /// </summary>
    public string? RecentLog { get; set; }
}

/// <summary>One frame of a relayed stack, from <c>System.Diagnostics.StackFrame</c>.</summary>
public class MobileDiagnosticsStackFrame
{
    /// <summary>0 for the outermost exception, 1 for its inner exception, and so on.</summary>
    public int Depth { get; set; }

    /// <summary>Position within that exception's stack, 0 at the throw site.</summary>
    public int Index { get; set; }

    public string? Type { get; set; }

    public string? Method { get; set; }

    public string? Assembly { get; set; }

    /// <summary>IL offset within the method, when known.</summary>
    public int? IlOffset { get; set; }

    /// <summary>Offset within the compiled native method, when known.</summary>
    public int? NativeOffset { get; set; }

    /// <summary>Instruction pointer as hex, when the runtime exposes one — the dSYM input.</summary>
    public string? NativeIp { get; set; }

    /// <summary>Base address of the native image the frame is in, as hex, when exposed.</summary>
    public string? NativeImageBase { get; set; }

    /// <summary>Source file and line, present only when the build shipped symbols the runtime reads.</summary>
    public string? File { get; set; }

    public int? Line { get; set; }
}
