using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// Builds the member-context half of a medical prompt: every registered
/// <see cref="IMemberContextSource"/> relevant to the prompt being written, rendered into labelled
/// sections in a fixed order.
/// </summary>
/// <remarks>
/// <para>
/// This is where the rules that must not drift between callers live. Each prompt service used to
/// assemble its own member context, and the differences between them were accidents rather than
/// decisions — one flattened caregiver notes and one did not, one carried environmental readings
/// and two did not. Centralising the assembly means a rule is enforced once for every prompt the
/// platform sends, including the ones written after the rule.
/// </para>
/// <para>
/// It composes member data only. The instruction block stays with its service and stays first in
/// the prompt: those blocks are the cacheable fixed prefix the serving engine reuses between calls
/// (docs/llm_design.md), and a prefix that varied per member would be no prefix at all.
/// </para>
/// </remarks>
public sealed partial class MemberContextComposer
{
    /// <summary>
    /// Ceiling on one section's body. Generous — this is the guard against a source that has
    /// somehow acquired an unbounded field, not the length any section aims at. A single CPU-served
    /// model has a finite context window and a real per-token cost, and the sections that matter
    /// most are the short ones; a runaway section must not be able to push them out.
    /// </summary>
    private const int MaxSectionLength = 4_000;

    /// <summary>Ceiling on a section label, which is a heading and never prose.</summary>
    private const int MaxLabelLength = 80;

    private readonly IEnumerable<IMemberContextSource> _sources;
    private readonly ILogger<MemberContextComposer> _logger;

    public MemberContextComposer(
        IEnumerable<IMemberContextSource> sources, ILogger<MemberContextComposer> logger)
    {
        _sources = sources;
        _logger = logger;
    }

    /// <summary>
    /// The member-context sections for one prompt, ready to drop in after the instructions.
    /// </summary>
    public async Task<string> ComposeAsync(MemberContextRequest request, CancellationToken ct)
    {
        // Ordered by the declared Order, then by type name so two sources sharing an Order still
        // land the same way in every host. DI enumeration order is not a contract, and a prompt
        // whose sections shuffle between the API and the pipeline is two different prompts.
        var applicable = _sources
            .Where(source => (source.Purposes & request.Purpose) != 0)
            .OrderBy(source => source.Order)
            .ThenBy(source => source.GetType().Name, StringComparer.Ordinal);

        var rendered = new List<string>();

        foreach (var source in applicable)
        {
            ct.ThrowIfCancellationRequested();

            MemberContextSection? section;
            try
            {
                // Sequential, not Task.WhenAll: sources share the request's DbContext, and EF Core
                // refuses a second operation while one is in flight. See the same note on
                // HealthInsightService.AnalyzeBaselineAsync, where starting these together threw.
                section = await source.BuildAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One contributor's failure must not cost the member their summary. The prompt goes
                // without that section, which is the same shape as the source having nothing to say
                // — every consumer already handles an absent section.
                _logger.LogError(
                    ex, "Member context source {Source} failed for CardiMember {CardiMemberId} "
                    + "building the {Purpose} prompt. The section is omitted.",
                    source.GetType().Name, request.CardiMemberId, request.Purpose);
                continue;
            }

            if (section is null || string.IsNullOrWhiteSpace(section.Body))
                continue;

            rendered.Add($"--- {SanitiseLabel(section.Label)} ---\n{SanitiseBody(section.Body)}");
        }

        return string.Join("\n\n", rendered);
    }

    /// <summary>
    /// A label is a heading, so it is reduced to one line and capped. Sources supply constants
    /// today; this is what keeps that true if one ever starts interpolating.
    /// </summary>
    private static string SanitiseLabel(string label)
    {
        var flattened = WhitespaceRuns().Replace(label, " ").Trim().Trim('-').Trim();
        if (flattened.Length == 0)
            return "Member context";

        return flattened.Length > MaxLabelLength ? flattened[..MaxLabelLength] : flattened;
    }

    /// <summary>
    /// Stops a body from forging a section of its own, then caps it.
    /// </summary>
    /// <remarks>
    /// Section bodies carry free text written by people and by external services — caregiver notes,
    /// questionnaire answers, a weather provider's description string. The instruction blocks tell
    /// the model that everything under a named section is background it must not take instructions
    /// from, and that framing is only worth something if the text underneath cannot end the section
    /// it was put in. A line beginning with the delimiter is defanged rather than dropped: the words
    /// are still what the caregiver wrote, and silently deleting a note reads as data loss.
    /// </remarks>
    private static string SanitiseBody(string body)
    {
        var builder = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            builder.Append(trimmed.TrimStart().StartsWith("---", StringComparison.Ordinal)
                ? trimmed.TrimStart().TrimStart('-').TrimStart()
                : trimmed);
            builder.Append('\n');
        }

        var sanitised = builder.ToString().TrimEnd('\n');
        return sanitised.Length > MaxSectionLength
            ? $"{sanitised[..MaxSectionLength]}… (truncated)"
            : sanitised;
    }

    [GeneratedRegex(@"[\s\p{Cc}]+")]
    private static partial Regex WhitespaceRuns();
}
