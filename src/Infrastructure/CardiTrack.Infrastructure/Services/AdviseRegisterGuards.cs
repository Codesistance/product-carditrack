namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The guards <see cref="AdviseGenerationService"/>'s reply is held to before it is persisted —
/// the code half of a boundary its prompt states and cannot enforce.
/// </summary>
/// <remarks>
/// <para>
/// Every other generation on this platform that may not say a certain kind of thing checks the
/// reply afterwards as well as asking for it beforehand: the journal has
/// <see cref="JournalRegisterGuards"/>, the digest has its own diagnostic and treatment markers for
/// a suggestion of the same length and shape as this one, and
/// <see cref="MedicalPromptBlocks.JournalNoCondition"/>'s remark states the reason in one line — a
/// prompt is a request and not a guarantee. Advise had only the request, which is the weakest place
/// on the platform for that to be true: it is the one generation whose whole purpose is to suggest
/// something, so it is the one whose reply most needs checking.
/// </para>
/// <para>
/// Markers are matched as substrings against the flattened, lowercased text, so a stem covers its
/// inflections ("diagnose", "diagnosed", "diagnosis"). They are deliberately not listed in the
/// prompt: a negative list there is a list MedGemma will echo, as
/// <see cref="MedicalPromptBlocks.CaregiverRegister"/>'s remark explains at length.
/// </para>
/// </remarks>
internal static class AdviseRegisterGuards
{
    /// <summary>
    /// Terms that name something the body is doing rather than something the watch recorded.
    /// </summary>
    /// <remarks>
    /// The digest's list rather than the journal's superset. Both guard the same boundary, but the
    /// journal's is tuned for a long account that is allowed precise clinical vocabulary and so
    /// reaches for stems this one would trip over; the digest's is tuned for a suggestion of
    /// roughly this length, and its remark records the calibration that matters here — the
    /// condition entries are compound phrases, never the bare word, because a suggestion can
    /// honestly mention warm conditions or a device in good condition.
    /// </remarks>
    private static readonly string[] ConditionMarkers =
    [
        "diagnos", "afib", "fibrillation", "arrhythmia",
        "medical condition", "heart condition", "health condition", "cardiac condition",
        "disease", "disorder", "syndrome",
    ];

    /// <summary>
    /// Phrasings that propose a treatment or a change to one — the prohibition
    /// <see cref="MedicalPromptBlocks.ToneWellnessNotClinical"/> states in words, stated again in
    /// code.
    /// </summary>
    /// <remarks>
    /// The journal's list, which is already the action shapes rather than the vocabulary: a
    /// suggestion may say "a short walk after lunch" and may not say "stop taking". Deliberately
    /// not the digest's <c>MedicalAdviceMarkers</c>, which bans "measure" and "blood pressure"
    /// outright — that list guards a question put to the family, where naming a measurement is
    /// itself the instruction, and it would discard an Advise suggestion for wording no caregiver
    /// would read as clinical.
    /// </remarks>
    private static readonly string[] TreatmentMarkers =
    [
        "start taking", "stop taking", "keep taking", "increase the dose", "reduce the dose",
        "lower the dose", "adjust the dose", "change the dose", "dosage", "prescrib",
        "prescription", "milligram", "should take",
    ];

    /// <summary>
    /// Replies to "which reference did you draw on" that name no reference. The prompt asks the
    /// model to leave this blank when nothing fits, and a model that will not leave a field empty
    /// fills it with one of these instead — which then passes a nonblank check and makes an
    /// ungrounded suggestion look grounded, defeating the traceability check the field exists for.
    /// </summary>
    /// <remarks>
    /// Matched whole, after trimming and stripping a trailing full stop, rather than as substrings:
    /// "none" inside "none of the sleep references fit" is a legitimate answer about the references,
    /// and "na" as a substring appears in ordinary words.
    /// </remarks>
    private static readonly string[] UngroundedCitations =
    [
        "n/a", "na", "n.a", "none", "nothing", "no reference", "no guideline", "unknown",
        "not applicable", "not specified", "-", "—",
    ];

    /// <summary>
    /// Phrases that are the briefs talking rather than a suggestion — the same backstop the
    /// digest's <c>InstructionEchoes</c> is, for the same model behaviour: MedGemma and the
    /// rewrite slot alike complete from the nearest text, and "It's just a suggestion, worth
    /// mentioning to their doctor" reached a caregiver's screen verbatim before this list
    /// existed. Each phrase sits wholly inside one line of the briefs so a reply that re-wraps
    /// the text still matches; matched case-insensitively against the flattened copy.
    /// </summary>
    /// <remarks>
    /// "Worth mentioning to their doctor" is deliberately here even though it is the register's
    /// own boundary line: the card renders that framing as fixed UI copy now, so a generation
    /// carrying it is burning its 25 words restating the chrome around it.
    /// </remarks>
    private static readonly string[] BriefEchoes =
    [
        "just a suggestion",
        "worth mentioning to their doctor",
        "grounded in the reference",
        "close that shortfall",
        "falls short of the reference",
        "an empty list",
        "clinical note",
        "everyday words",
    ];

    /// <summary>
    /// True when the text names a condition or proposes a treatment — either way, a reply the
    /// caller must withhold rather than serve to a caregiver.
    /// </summary>
    /// <remarks>
    /// For text on its way to a person. The clinical read is checked with
    /// <see cref="ProposesTreatment"/> instead: the condition half of this is a register boundary
    /// and belongs where the register does, on the output.
    /// </remarks>
    internal static bool ReadsAsClinical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var flattened = MedicalPromptBlocks.Flatten(text);
        return ConditionMarkers.Any(m => flattened.Contains(m, StringComparison.OrdinalIgnoreCase))
            || ProposesTreatment(text);
    }

    /// <summary>
    /// True when the text proposes a treatment or a change to one — the half of
    /// <see cref="ReadsAsClinical"/> that still applies to the clinical read.
    /// </summary>
    /// <remarks>
    /// Split out when the clinical brief was unclamped, because the two markers were guarding two
    /// different things under one name. Naming a condition is a <em>register</em> boundary: it
    /// says what a family may be told, so it belongs on the text a family reads. Proposing a
    /// treatment is a <em>scope</em> boundary: Advise exists to suggest an everyday action, and a
    /// dose change is not one at any stage of the pipeline — a rewrite asked to carry a note
    /// about stopping a medication has already been handed the wrong note.
    /// </remarks>
    internal static bool ProposesTreatment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var flattened = MedicalPromptBlocks.Flatten(text);
        return TreatmentMarkers.Any(m => flattened.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the rewritten copy is the brief restating itself rather than this member's
    /// suggestion — see <see cref="BriefEchoes"/>.
    /// </summary>
    internal static bool EchoesTheBrief(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var flattened = MedicalPromptBlocks.Flatten(text);
        return BriefEchoes.Any(m => flattened.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when a rewritten summary quotes a figure. The summary's job is what has been noticed
    /// in everyday words — "steps rose from 3949 to 9099" is a lab note, and the figures already
    /// live on the trend cards below it. Applied to the summary only: a suggestion may honestly
    /// carry "15-20 minutes".
    /// </summary>
    internal static bool QuotesAFigure(string? summary) =>
        !string.IsNullOrWhiteSpace(summary) && summary.Any(char.IsDigit);

    /// <summary>
    /// True when the cited reference names nothing — a blank field, or one of the placeholders a
    /// model reaches for instead of leaving it blank.
    /// </summary>
    internal static bool IsUngroundedCitation(string? guidelineCited)
    {
        if (string.IsNullOrWhiteSpace(guidelineCited))
            return true;

        var normalised = MedicalPromptBlocks.Flatten(guidelineCited).Trim().TrimEnd('.').Trim();
        return UngroundedCitations.Contains(normalised, StringComparer.OrdinalIgnoreCase);
    }
}
