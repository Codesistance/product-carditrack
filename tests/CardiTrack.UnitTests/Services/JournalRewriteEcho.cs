using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Wires a Rewrite-slot fake for the three CardiJournal books that echoes the clinical read it was
/// handed back as the family's account.
/// </summary>
/// <remarks>
/// The books' tests are almost all about what ends up stored — the guards, the placeholder, the
/// sentence floor, the urgency — rather than about which model wrote it, and echoing keeps them
/// saying what they always said. It reads the finding out of the prompt rather than returning a
/// fixed string, so a read that stops crossing the slot boundary fails these tests instead of
/// passing through them.
/// </remarks>
internal static class JournalRewriteEcho
{
    internal static void Wire(
        IRewriteAiService rewriteAi,
        string headline = "A settled stretch",
        string suggestion = "Ask how they have been sleeping lately.") =>
        // The WithUsage variant, because a book's Rewrite call is billed: the ledger records a row
        // per call, so the production path needs the usage back and a fake that only stubs the
        // usage-less overload silently returns null and discards every book.
        rewriteAi.GenerateStructuredWithUsageAsync<JournalRewritePrompt.JournalRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new AiGenerationResult<JournalRewritePrompt.JournalRewriteAiResponse>(
                new JournalRewritePrompt.JournalRewriteAiResponse
                {
                    Summary = FindingIn((string)call[0]!),
                    Headline = headline,
                    Suggestion = suggestion,
                },
                new AiUsage { ModelName = "test-rewrite", InputTokens = 400, OutputTokens = 90 }));

    /// <summary>The read's own finding line, which is everything after its label.</summary>
    private static string FindingIn(string prompt)
    {
        const string label = "finding: ";
        var at = prompt.LastIndexOf(label, StringComparison.Ordinal);
        return at < 0 ? string.Empty : prompt[(at + label.Length)..].Trim();
    }
}
