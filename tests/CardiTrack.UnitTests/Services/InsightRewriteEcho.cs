using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Wires a Rewrite-slot fake that echoes back the clinical read it was handed, for the five
/// fixtures that exercise <see cref="HealthInsightService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The split put a second model between the clinical read and the card, and almost every test in
/// those fixtures is about what ends up on the card rather than about which model wrote it.
/// Echoing keeps those assertions saying what they always said. A fixed stub would have worked
/// too, and would have been worse: it would have passed just as happily if the read never reached
/// the rewrite at all.
/// </para>
/// <para>
/// Because it parses the prompt rather than reading a captured variable, it also quietly asserts
/// the prompt's shape — if <c>BuildRewritePrompt</c> stops carrying the read under these labels,
/// every one of these tests notices.
/// </para>
/// </remarks>
internal static class InsightRewriteEcho
{
    internal static void Wire(IRewriteAiService rewriteAi)
    {
        rewriteAi.GenerateStructuredAsync<HealthInsightService.AlertAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new HealthInsightService.AlertAiResponse
            {
                Explanation = Field((string)call[0]!, "finding: "),
                RecommendedAction = Field((string)call[0]!, "suggested action: "),
            });

        rewriteAi.GenerateStructuredAsync<HealthInsightService.BaselineAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var read = ReadBody((string)call[0]!);
                var at = read.IndexOf("findings:", StringComparison.Ordinal);
                return new HealthInsightService.BaselineAiResponse
                {
                    Summary = Field(read, "summary: "),
                    KeyFindings = at < 0
                        ? []
                        : read[(at + "findings:".Length)..]
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(line => line.TrimStart('-', ' '))
                            .Where(line => line.Length > 0)
                            .ToList(),
                };
            });
    }

    /// <summary>Everything after the prompt's read marker — the part the boundary let through.</summary>
    private static string ReadBody(string prompt)
    {
        const string marker = "--- Clinical read to write from ---";
        var at = prompt.LastIndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? string.Empty : prompt[(at + marker.Length)..].Trim();
    }

    /// <summary>One labelled line of the read, to the end of the line or the end of the read.</summary>
    private static string Field(string text, string label)
    {
        var body = text.Contains("--- Clinical read", StringComparison.Ordinal) ? ReadBody(text) : text;
        var at = body.IndexOf(label, StringComparison.Ordinal);
        if (at < 0)
            return string.Empty;

        var from = at + label.Length;
        var newline = body.IndexOf('\n', from);
        return (newline < 0 ? body[from..] : body[from..newline]).Trim();
    }
}
