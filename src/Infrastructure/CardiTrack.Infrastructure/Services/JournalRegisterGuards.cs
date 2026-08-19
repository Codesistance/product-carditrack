using System.Text.RegularExpressions;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The guards every CardiJournal book's reply is held to, whatever period it covers.
/// </summary>
/// <remarks>
/// <para>
/// The register is a property of the journal, not of the Daybook: a Weekbook may name a
/// measurement and may not name a condition for exactly the reasons the Daybook may not, and a
/// line drawn once and enforced in one place cannot drift between books. Extracted here when the
/// Weekbook arrived; <see cref="DaybookPrompt"/> keeps its own methods as the names its tests and
/// its generator already use, and forwards to these.
/// </para>
/// <para>
/// The instruction-echo check is the exception and stays per-prompt: its list is drawn from the
/// wording of one brief, so a book can only be checked against its own.
/// </para>
/// </remarks>
internal static partial class JournalRegisterGuards
{
    /// <summary>
    /// Terms that name something the body is doing rather than something the watch recorded. This
    /// is the line the journal's whole allowance turns on: naming a measurement describes what was
    /// measured, naming a condition is an inference about the person, and CardiTrack does not
    /// diagnose (docs/solution_manifest.md). A reply containing one of these is discarded whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supersets <c>DigestGenerationService.DiagnosticMarkers</c> rather than sharing it: that list
    /// guards a 25-word suggestion and can afford bare stems, while this one reads a long account
    /// and needs the clinical vocabulary a more precise register can actually reach for.
    /// </para>
    /// <para>
    /// The inference phrasings at the end matter as much as the condition names. "A sign of" needs
    /// no condition after it to be diagnosis — it asserts that a reading means something about the
    /// body, which is exactly the claim this product may not make, and it is the shape a model
    /// reaches for when it has been allowed precise words and wants to sound useful.
    /// </para>
    /// </remarks>
    private static readonly string[] ConditionMarkers =
    [
        // Named conditions and their stems.
        "diagnos", "afib", "fibrillation", "arrhythmia", "atrial", "apnoea", "apnea",
        "hypoxaem", "hypoxem", "bradycard", "tachycard", "hypertens", "hypotens",
        "ischaem", "ischem", "angina", "infarct", "insufficiency", "dementia", "delirium",
        "disease", "disorder", "syndrome", "medical condition", "heart condition",
        "health condition", "cardiac condition", "underlying condition",
        // Diagnostic inference, with or without a condition named after it.
        "a sign of", "signs of", "a symptom of", "symptoms of", "indicative of",
        "suggestive of", "points to a", "may indicate", "could indicate",
    ];

    // "Consistent with" is deliberately absent, though it is the clinical inference phrase par
    // excellence. Every book instructs the model to say where a reading sat against the member's
    // own usual, and "consistent with her usual 58" is a natural way to answer that — so the
    // marker collides with the instruction directly. A book is written once and never retried,
    // which makes a false discard cost the caregiver that period entirely, and the phrasings left
    // above catch the same claim when it is actually about the body.

    /// <summary>
    /// Phrasings that propose a treatment. Narrower than
    /// <c>DigestGenerationService.MedicalAdviceMarkers</c>, which guards a question and can ban
    /// "measure" and "blood pressure" outright — words a journal entry says legitimately and often,
    /// because saying what was measured is its whole job. These are the action shapes instead.
    /// </summary>
    private static readonly string[] TreatmentMarkers =
    [
        "start taking", "stop taking", "keep taking", "increase the dose", "reduce the dose",
        "lower the dose", "adjust the dose", "change the dose", "dosage", "prescrib",
        "prescription", "milligram", "should take",
    ];

    /// <summary>
    /// Terms a family reader is not expected to know, which the register therefore requires to
    /// explain themselves in the sentence that first uses them.
    /// </summary>
    /// <remarks>
    /// Deliberately short, and deliberately excludes terms that are precise but already plain —
    /// resting heart rate, deep sleep, active minutes, steps. Requiring a gloss on those would
    /// discard good entries for explaining what needs no explaining, and would train the register
    /// toward the padding the gloss rule exists to prevent. What is left is the vocabulary a GP
    /// uses and a caregiver does not.
    /// </remarks>
    private static readonly string[] TermsNeedingAGloss =
    [
        "sleep efficiency", "sleep latency", "rem sleep", "rem ", "spo2", "spo₂",
        "oxygen saturation", "respiratory rate", "vo2", "vo₂", "heart rate variability",
        "hrv", "sedentary", "nadir", "diurnal", "circadian", "arrhythmi", "perfusion",
    ];

    /// <summary>
    /// What makes a sentence explain its own term. Generous on purpose: the rule being enforced is
    /// "the reader can tell what this measures", not a particular sentence construction, and a
    /// guard stricter than the rule would discard entries that had in fact complied.
    /// </summary>
    private static readonly string[] GlossMarkers =
    [
        "—", "–", "(", "which is", "which measures", "which counts", "which tracks",
        "meaning", "that is", "in other words", "the share of", "the proportion of",
        "the amount of", "how much", "how long", "how often", "a measure of", ", or ",
    ];

    /// <summary>
    /// Whether the reply is the brief read back rather than an account of anything.
    /// </summary>
    /// <param name="echoes">
    /// Phrases that appear only in the prompt that produced this reply. Per-book, because each
    /// brief is worded differently and a book can only be checked against its own.
    /// </param>
    internal static bool ReadsLikeInstructions(string text, IReadOnlyList<string> echoes)
    {
        var flattened = Flatten(text);
        return echoes.Any(echo => flattened.Contains(echo, StringComparison.Ordinal));
    }

    /// <summary>
    /// The condition or treatment phrase that makes this reply a diagnosis, or null when it names
    /// only what was measured. Returned rather than a bool so the discard can say which word cost
    /// the generation — the list is the product's regulatory line and needs to be tunable from
    /// what it actually catches.
    /// </summary>
    internal static string? NamesACondition(string text)
    {
        var flattened = Flatten(text);
        return ConditionMarkers.Concat(TreatmentMarkers)
            .FirstOrDefault(marker => flattened.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The first precise term used without explaining itself, or null when every one of them did.
    /// </summary>
    /// <remarks>
    /// Judged on first use only, which is what the register asks for: a term explained in sentence
    /// three may be used bare in sentence nine, and requiring the gloss every time would produce
    /// exactly the repetitive padding this rule exists to avoid. Sentences are split on terminal
    /// punctuation, so the gloss has to sit in the same sentence as the term — a definition two
    /// sentences later is not one the reader meets in time to use.
    /// </remarks>
    internal static string? UnglossedTerm(string text)
    {
        var sentences = SentenceEnds().Split(Flatten(text))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        foreach (var term in TermsNeedingAGloss)
        {
            var first = sentences.FirstOrDefault(s => s.Contains(term, StringComparison.Ordinal));
            if (first is null)
                continue;

            if (!GlossMarkers.Any(marker => first.Contains(marker, StringComparison.Ordinal)))
                return term.Trim();
        }

        return null;
    }

    /// <summary>
    /// A sentence boundary: terminal punctuation, except a full stop with a digit on both sides —
    /// this text quotes figures by design, and splitting "95.4%" in half moved a term and the
    /// gloss that follows its figure into different fragments, discarding a compliant entry. An
    /// entry is written once, so that false positive cost the caregiver the period for good.
    /// </summary>
    [GeneratedRegex(@"[!?;]|(?<!\d)\.|\.(?!\d)")]
    private static partial Regex SentenceEnds();

    /// <summary>
    /// Lowercased with runs of whitespace collapsed, so a phrase the model wrapped across two
    /// lines still matches the single-line phrase being looked for.
    /// </summary>
    internal static string Flatten(string text) =>
        string.Join(' ', text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
