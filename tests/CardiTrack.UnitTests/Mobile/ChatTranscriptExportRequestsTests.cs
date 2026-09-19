using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The snapshot a chat transcript export confirms and then generates. The one thing that must
/// hold is that the two agree: a generate whose fingerprint differs from the confirmation's is
/// refused outright, and the caregiver is told their export "no longer matches what you
/// confirmed" having changed nothing.
/// </summary>
public class ChatTranscriptExportRequestsTests
{
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    private static readonly DateOnly Started = new(2026, 2, 10);
    private static readonly DateOnly LastTurn = new(2026, 2, 11);

    [Fact]
    public void ConsentAndGenerate_MintTheSameFingerprint()
    {
        var consent = ChatTranscriptExportRequests.Consent(
            MemberId, SessionId, Started, LastTurn, ReportFormat.Pdf, ExportConsentMethod.Password);
        var generate = ChatTranscriptExportRequests.Generate(
            MemberId, SessionId, "Margaret — chat", Started, LastTurn, ReportFormat.Pdf, "token");

        Assert.Equal(
            ExportConsentPolicy.Fingerprint(ToGenerate(consent)),
            ExportConsentPolicy.Fingerprint(generate));
    }

    [Fact]
    public void Generate_NamesTheConversationAndNothingElse()
    {
        var generate = ChatTranscriptExportRequests.Generate(
            MemberId, SessionId, null, Started, LastTurn, ReportFormat.Csv, "token");

        Assert.Equal(SessionId, generate.ChatSessionId);
        Assert.Equal([MemberId], generate.CardiMemberIds);
        Assert.False(generate.IncludeMetrics);
        Assert.False(generate.IncludeTrends);
        Assert.False(generate.IncludeAlerts);
        Assert.False(generate.IncludeJournals);
        Assert.Null(generate.JournalAudience);
    }

    [Fact]
    public void Window_OrdersTheEnds()
    {
        // A clock skew between a session's start and its last turn must not produce a range the
        // API refuses.
        var (from, to) = ChatTranscriptExportRequests.Window(LastTurn, Started);

        Assert.Equal(Started, from);
        Assert.Equal(LastTurn, to);
    }

    [Fact]
    public void Window_KeepsAOneDayConversationOnItsOwnDay()
    {
        var (from, to) = ChatTranscriptExportRequests.Window(Started, Started);

        Assert.Equal(Started, from);
        Assert.Equal(Started, to);
    }

    [Fact]
    public void Window_TrimsTheStart_WhenAReopenedThreadOutrunsTheCeiling()
    {
        // Continuing a conversation from last year brings its start with it. The ceiling trims
        // the start, not the end: the recent turns are the ones being exported.
        var (from, to) = ChatTranscriptExportRequests.Window(new DateOnly(2024, 1, 1), LastTurn);

        Assert.Equal(LastTurn, to);
        Assert.Equal(JournalExportRequests.MaxRangeDays, to.DayNumber - from.DayNumber + 1);
    }

    /// <summary>The same mapping <c>ExportConsentFlow.ToConsent</c> does in reverse — what the
    /// API fingerprints when the consent is recorded.</summary>
    private static Application.DTOs.Requests.GenerateReportRequest ToGenerate(
        Application.DTOs.Requests.RecordExportConsentRequest consent) => new()
    {
        CardiMemberIds = consent.CardiMemberIds,
        DateRangeFrom = consent.DateRangeFrom,
        DateRangeTo = consent.DateRangeTo,
        Format = consent.Format,
        IncludeMetrics = consent.IncludeMetrics,
        IncludeTrends = consent.IncludeTrends,
        IncludeAlerts = consent.IncludeAlerts,
        IncludeJournals = consent.IncludeJournals,
        IncludeNotices = consent.IncludeNotices,
        IncludeDevices = consent.IncludeDevices,
        JournalEntryDate = consent.JournalEntryDate,
        JournalAudience = consent.JournalAudience,
        ChatSessionId = consent.ChatSessionId,
    };
}
