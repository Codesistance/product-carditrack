using CardiTrack.API.Controllers;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Observability;
using CardiTrack.Shared.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace CardiTrack.IntegrationTests.MobileDiagnostics;

/// <summary>
/// The endpoint's job is to turn a phone's log line into a Datadog log line under the mobile
/// service, and to refuse everyone else. So: the three refusals (no key configured, no key
/// presented, wrong key), the ceilings, and — the point of it all — that a relayed crash comes
/// out the other side carrying the properties the runbook says to query on.
/// </summary>
public class MobileDiagnosticsEndpointTests
{
    private const string Key = "yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=";

    private readonly List<LogEvent> _events = [];

    private sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private MobileDiagnosticsController CreateSut(string? configuredKey = Key, string? presentedKey = Key)
    {
        // A real Serilog pipeline behind the ILogger<T>, with FromLogContext on: the relayed
        // properties travel through LogContext, and a substitute logger would never see them.
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(new ListSink(_events))
            .CreateLogger();
        var logger = new SerilogLoggerFactory(serilog).CreateLogger<MobileDiagnosticsController>();

        var sut = new MobileDiagnosticsController(
            MobileDiagnosticsKey.FromConfiguration(configuredKey),
            new MobileDiagnosticsLogValidator(),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        if (presentedKey is not null)
            sut.Request.Headers[MobileDiagnosticsContract.KeyHeader] = presentedKey;
        return sut;
    }

    private static MobileDiagnosticsLogRequest Batch(params MobileDiagnosticsLogEntry[] entries) => new()
    {
        Platform = "ios",
        AppVersion = "0.2.304+1512",
        Device = "Apple iPhone17,1",
        Manufacturer = "Apple",
        Model = "iPhone17,1",
        OsVersion = "26.6.1",
        OsDescription = "Darwin 25.6.0",
        Architecture = "Arm64",
        Runtime = ".NET 10.0.1",
        Locale = "en-GB",
        TimeZone = "Europe/London",
        AppAssemblyVersion = "0.2.304",
        ModuleVersionId = "0123456789abcdef0123456789abcdef",
        InstallId = "abc123",
        Entries = [.. entries],
    };

    private static MobileDiagnosticsLogEntry Entry(string level = "Error", string? exception = null) => new()
    {
        Timestamp = new DateTimeOffset(2026, 9, 14, 21, 19, 26, TimeSpan.Zero),
        Level = level,
        Message = "Unhandled exception (terminating: True)",
        Exception = exception,
        Source = "CardiTrack.Mobile.Unhandled",
        Screen = "SignInPage",
        NetworkAccess = "Internet",
        UptimeSeconds = 118.2,
        ThreadId = 1,
        IsMainThread = true,
        ManagedMemoryBytes = 12_345_678,
    };

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    // ── Refusals ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoKeyConfigured_Is404_EvenWithTheRightHeader()
    {
        var result = await CreateSut(configuredKey: null).Post(Batch(Entry()), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(_events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-key")]
    public async Task MissingOrWrongKey_Is401AndLogsNothing(string? presented)
    {
        var result = await CreateSut(presentedKey: presented).Post(Batch(Entry()), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_events);
    }

    // ── Ceilings ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyBatch_Is400()
    {
        var result = await CreateSut().Post(Batch(), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = Assert.IsType<ErrorResponse>(bad.Value);
        Assert.NotEmpty(error.Errors);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task TooManyEntries_Is400()
    {
        var entries = Enumerable.Range(0, MobileDiagnosticsContract.MaxEntriesPerBatch + 1)
            .Select(_ => Entry())
            .ToArray();

        var result = await CreateSut().Post(Batch(entries), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task TooManyFrames_Is400()
    {
        var entry = Entry();
        entry.Frames = Enumerable.Range(0, MobileDiagnosticsContract.MaxFrames + 1)
            .Select(i => new MobileDiagnosticsStackFrame { Index = i })
            .ToList();

        var result = await CreateSut().Post(Batch(entry), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task UnknownLevel_Is400()
    {
        var result = await CreateSut().Post(Batch(Entry(level: "Verbose")), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_events);
    }

    // ── The relay itself ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidBatch_Is202AndRelaysEveryEntryUnderTheMobileService()
    {
        const string exception =
            "System.InvalidOperationException: Sequence contains no elements\n" +
            "   at CardiTrack.Mobile.SignInPage.OnSignInClicked(Object sender, EventArgs e)";
        var crashEntry = Entry("Fatal", exception);
        crashEntry.ExceptionType = "System.InvalidOperationException";
        crashEntry.ExceptionMessage = "Sequence contains no elements";
        crashEntry.Frames =
        [
            new MobileDiagnosticsStackFrame
            {
                Depth = 0,
                Index = 0,
                Type = "System.Linq.Enumerable",
                Method = "TSource First[TSource](System.Collections.Generic.IEnumerable`1[TSource])",
                Assembly = "System.Linq",
                IlOffset = 12,
                NativeIp = "0x1033795b8",
                NativeImageBase = "0x100f20000",
            },
            new MobileDiagnosticsStackFrame
            {
                Depth = 0,
                Index = 1,
                Type = "CardiTrack.Mobile.SignInPage",
                Method = "Void OnSignInClicked(System.Object, System.EventArgs)",
                Assembly = "CardiTrack.Mobile",
                IlOffset = 31,
            },
        ];
        crashEntry.RecentLog = "2026-09-14 22:19:20.001 +01:00 [WRN] v0.2.304 SecureTokenStore: write timed out\n";

        var result = await CreateSut().Post(Batch(crashEntry, Entry("Error")), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(accepted.Value);
        Assert.True(response.Success);
        Assert.Contains("2 entries", response.Message);

        Assert.Equal(2, _events.Count);
        Assert.All(_events, e => Assert.True(LogRelay.IsRelayedTo(e, ApmServiceNames.Mobile)));

        var crash = _events[0];
        Assert.Equal(LogEventLevel.Fatal, crash.Level);
        Assert.Equal("ios", Scalar(crash, "ClientPlatform"));
        Assert.Equal("0.2.304+1512", Scalar(crash, "ClientVersion"));
        Assert.Equal("Apple iPhone17,1", Scalar(crash, "MobileDevice"));
        Assert.Equal("Apple", Scalar(crash, "MobileManufacturer"));
        Assert.Equal("iPhone17,1", Scalar(crash, "MobileModel"));
        Assert.Equal("26.6.1", Scalar(crash, "MobileOs"));
        Assert.Equal("Darwin 25.6.0", Scalar(crash, "MobileOsDescription"));
        Assert.Equal("Arm64", Scalar(crash, "MobileArchitecture"));
        Assert.Equal(".NET 10.0.1", Scalar(crash, "MobileRuntime"));
        Assert.Equal("en-GB", Scalar(crash, "MobileLocale"));
        Assert.Equal("Europe/London", Scalar(crash, "MobileTimeZone"));
        Assert.Equal("0.2.304", Scalar(crash, "MobileAssemblyVersion"));
        Assert.Equal("0123456789abcdef0123456789abcdef", Scalar(crash, "MobileModuleVersionId"));
        Assert.Equal("abc123", Scalar(crash, "MobileInstallId"));
        Assert.Equal("CardiTrack.Mobile.Unhandled", Scalar(crash, "MobileSource"));
        Assert.Equal("SignInPage", Scalar(crash, "MobileScreen"));
        Assert.Equal("Internet", Scalar(crash, "MobileNetwork"));
        Assert.Equal("118.2", Scalar(crash, "MobileUptimeSeconds"));
        Assert.Equal("1", Scalar(crash, "MobileThreadId"));
        Assert.Equal("True", Scalar(crash, "MobileIsMainThread"));
        Assert.Equal("12345678", Scalar(crash, "MobileManagedMemoryBytes"));
        Assert.StartsWith("2026-09-14 22:19:20.001", Scalar(crash, "MobileRecentLog"));
        Assert.Equal("System.InvalidOperationException", Scalar(crash, "error.kind"));
        Assert.Equal("Sequence contains no elements", Scalar(crash, "error.message"));
        Assert.Equal(exception.Replace('\n', ' '), Scalar(crash, "error.stack"));
        Assert.Contains("Unhandled exception (terminating: True)", crash.RenderMessage());

        // Frames are structured, not flattened: two frames, each with its fields.
        var frames = Assert.IsType<SequenceValue>(crash.Properties["MobileFrames"]);
        Assert.Equal(2, frames.Elements.Count);
        var first = Assert.IsType<StructureValue>(frames.Elements[0]);
        Assert.Contains(first.Properties, p => p.Name == "Type" && p.Value.ToString().Contains("System.Linq.Enumerable"));
        Assert.Contains(first.Properties, p => p.Name == "NativeIp" && p.Value.ToString().Contains("0x1033795b8"));
        var second = Assert.IsType<StructureValue>(frames.Elements[1]);
        Assert.Contains(second.Properties, p => p.Name == "Type" && p.Value.ToString().Contains("CardiTrack.Mobile.SignInPage"));

        var plain = _events[1];
        Assert.Equal(LogEventLevel.Error, plain.Level);
        Assert.False(plain.Properties.ContainsKey("error.stack"));
        Assert.False(plain.Properties.ContainsKey("MobileFrames"));
        Assert.False(plain.Properties.ContainsKey("MobileRecentLog"));
    }

    /// <summary>
    /// A build that predates the structured fields still gets a usable error.kind/error.message:
    /// parsed from the first line of the exception text.
    /// </summary>
    [Fact]
    public async Task ExceptionTypeAndMessage_FallBackToTheFirstLineOfTheText()
    {
        await CreateSut().Post(
            Batch(Entry("Fatal", "System.NullReferenceException: Object reference not set\n   at X")),
            CancellationToken.None);

        var crash = Assert.Single(_events);
        Assert.Equal("System.NullReferenceException", Scalar(crash, "error.kind"));
        Assert.Equal("Object reference not set", Scalar(crash, "error.message"));
    }

    [Fact]
    public async Task Warning_RelaysAsWarning()
    {
        await CreateSut().Post(Batch(Entry("Warning")), CancellationToken.None);

        Assert.Equal(LogEventLevel.Warning, Assert.Single(_events).Level);
    }

    /// <summary>
    /// The pushed properties are scoped to the relayed write. A line the API writes for itself
    /// afterwards must not inherit the mobile service, or it would ship under the wrong name.
    /// </summary>
    [Fact]
    public async Task RelayScope_DoesNotLeakIntoLaterEvents()
    {
        var sut = CreateSut();
        await sut.Post(Batch(Entry()), CancellationToken.None);

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(new ListSink(_events))
            .CreateLogger();
        serilog.Information("the API's own line");

        Assert.Equal(2, _events.Count);
        Assert.False(LogRelay.IsRelayed(_events[1]));
    }
}
