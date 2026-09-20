namespace CardiTrack.Application.Services;

/// <summary>
/// How much of a generated insight the store will hold, and the one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// The columns are generous rather than tight — they guard against a runaway value, not against
/// style — but generous is not unbounded, and the model is allowed a 2,048-token completion. A
/// reply that is valid in every other way but longer than the column would fail
/// <c>SaveChangesAsync</c>, which costs the member the insight <em>and</em> leaves the row a
/// permanent backfill candidate: the write fails again on every retry, for as long as the model
/// keeps answering at that length.
/// </para>
/// <para>
/// So the writers fit the text to the column rather than hoping, and both the writers and
/// <c>MemberInsightConfiguration</c> read the budgets from here. Two copies of a number that must
/// agree is the shape of the bug this prevents.
/// </para>
/// </remarks>
public static class InsightLimits
{
    /// <summary>Characters the summary column holds.</summary>
    public const int Summary = 2000;

    /// <summary>Characters the recommended-action column holds.</summary>
    public const int RecommendedAction = 1000;

    /// <summary>Characters the joined key-findings column holds.</summary>
    public const int KeyFindings = 2000;

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="limit"/>, at a sentence end where there is
    /// one.
    /// </summary>
    /// <remarks>
    /// A caregiver reading an insight that stops mid-clause has been shown the product failing,
    /// so an overlong reply is trimmed back to its last complete sentence rather than chopped at
    /// the character. Only where no sentence ends inside the budget — a model that answered in one
    /// enormous run-on — does it fall back to a hard cut with an ellipsis, which at least reads as
    /// deliberate. Null and anything already inside the budget pass straight through, which is
    /// every real reply.
    /// </remarks>
    public static string? Fit(string? text, int limit)
    {
        if (text is null || text.Length <= limit)
            return text;

        var cut = text[..limit];
        var lastSentenceEnd = cut.LastIndexOfAny(['.', '!', '?']);

        // Only if it leaves something worth reading. A sentence end in the first few characters
        // means the budget landed inside an opening abbreviation, not at a usable break.
        return lastSentenceEnd >= limit / 2
            ? cut[..(lastSentenceEnd + 1)]
            : string.Concat(cut[..(limit - 1)].TrimEnd(), "…");
    }

    /// <summary>
    /// The findings joined one per line, dropping any that would not fit whole.
    /// </summary>
    /// <remarks>
    /// Dropped rather than truncated, unlike <see cref="Fit"/>: a findings block is a list of
    /// separate claims, and half a claim is not a shorter claim. Null when nothing is left, which
    /// is what the column holds for a member with no findings anyway.
    /// </remarks>
    public static string? JoinFindings(IEnumerable<string> findings)
    {
        var kept = new List<string>();
        var length = 0;

        foreach (var finding in findings)
        {
            // The newline this one would bring with it, for every finding after the first.
            var cost = finding.Length + (kept.Count > 0 ? 1 : 0);
            if (length + cost > KeyFindings)
                continue;

            kept.Add(finding);
            length += cost;
        }

        return kept.Count > 0 ? string.Join('\n', kept) : null;
    }
}
