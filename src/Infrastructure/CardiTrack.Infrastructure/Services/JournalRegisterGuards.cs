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
    /// The plain-words explanation written in for each term of <see cref="TermsNeedingAGloss"/>
    /// the model uses bare, spelled exactly as that list spells them (so the two agree on what
    /// counts as a use) and in the order they are tried: a phrase precedes its stem ("rem sleep"
    /// before "rem", "circadian rhythm" before "circadian") so the gloss lands after the whole
    /// phrase rather than inside it. <c>arrhythmi</c> has no entry: it is a condition, and
    /// <see cref="NamesACondition"/> has already refused the reply by the time this runs.
    /// </summary>
    /// <remarks>
    /// Written in code rather than asked for again, because asking again was the failure. The
    /// brief asks for the gloss and the model gives it perhaps one time in five; the discard that
    /// followed did not make the next attempt any likelier to comply, it only selected — across a
    /// day of half-hourly retries — for the reply that named the fewest readings, which is the
    /// reply that said the least. Each explanation says what the term measures and nothing about
    /// what the figure means, so writing it in cannot add a claim the account did not make.
    /// </remarks>
    private static readonly (string Term, string Gloss)[] Glosses =
    [
        ("sleep efficiency", "the share of time in bed actually spent asleep"),
        ("sleep latency", "how long it took to fall asleep"),
        ("rem sleep", "the dreaming stage of sleep"),
        ("rem ", "the dreaming stage of sleep"),
        ("oxygen saturation", "the oxygen level in the blood"),
        ("spo2", "the oxygen level in the blood"),
        ("spo₂", "the oxygen level in the blood"),
        ("respiratory rate", "breaths a minute"),
        ("vo2", "how much oxygen the body can use when working hard"),
        ("vo₂", "how much oxygen the body can use when working hard"),
        ("heart rate variability", "the natural variation in the gap between one heartbeat and the next"),
        ("hrv", "the natural variation in the gap between one heartbeat and the next"),
        ("sedentary", "sitting or lying still"),
        ("nadir", "the lowest point"),
        ("diurnal", "across the day"),
        ("circadian rhythm", "the body's built-in daily clock"),
        ("circadian", "the body's built-in daily clock"),
        ("perfusion", "blood flow through the tissues"),
    ];

    /// <summary>
    /// The fewest sentences an account of a week or a month may run to. Both briefs ask for six
    /// or more and allow an unremarkable period a short account; below three the reply is not an
    /// account of anything, whatever it says — it is the single line the discard-and-retry loop
    /// used to select for.
    /// </summary>
    internal const int MinimumSentences = 3;

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
        var sentences = Sentences(text);

        foreach (var term in TermsNeedingAGloss)
        {
            if (IsUnglossed(sentences, term))
                return term.Trim();
        }

        return null;
    }

    /// <summary>
    /// The reply with a plain-words explanation written in after the first use of each precise
    /// term it left bare, and the terms so treated. Unchanged, with no terms, when every term
    /// already explained itself — the brief's own gloss is always kept over this one.
    /// </summary>
    /// <remarks>
    /// The gloss goes straight after the term, in brackets, which is one of the shapes
    /// <see cref="UnglossedTerm"/> accepts — so a reply this has touched passes that guard, and a
    /// term it has no explanation for still fails it. Whole words only: "rem" must not fire inside
    /// "remained", and the same insertion made a second time would re-explain a term the first
    /// pass already explained.
    /// </remarks>
    internal static (string Text, IReadOnlyList<string> Glossed) Gloss(string text)
    {
        var result = text;
        var glossed = new List<string>();

        foreach (var (term, gloss) in Glosses)
        {
            if (!IsUnglossed(Sentences(result), term))
                continue;

            var match = WholeTerm(term.Trim()).Match(result);
            if (!match.Success)
                continue;

            result = result.Insert(match.Index + match.Length, $" ({gloss})");
            glossed.Add(term.Trim());
        }

        return (result, glossed);
    }

    /// <summary>How many sentences the reply runs to, split the way the gloss rule splits them.</summary>
    internal static int SentenceCount(string text) => Sentences(text).Count;

    /// <summary>
    /// Whether the first sentence using <paramref name="term"/> leaves it unexplained. False when
    /// no sentence uses it at all.
    /// </summary>
    private static bool IsUnglossed(IReadOnlyList<string> sentences, string term)
    {
        var first = sentences.FirstOrDefault(s => s.Contains(term, StringComparison.Ordinal));
        return first is not null
            && !GlossMarkers.Any(marker => first.Contains(marker, StringComparison.Ordinal));
    }

    private static List<string> Sentences(string text) =>
        SentenceEnds().Split(Flatten(text))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

    /// <summary>
    /// <paramref name="term"/> as whole words in the reply's own casing and line breaks: bounded
    /// by anything that is not a letter or digit (a plain <c>\b</c> would not close after the
    /// subscript two in "SpO₂"), with a run of whitespace allowed wherever the term has a space.
    /// </summary>
    private static Regex WholeTerm(string term) =>
        new(
            @"(?<![\p{L}\p{N}])" + Regex.Escape(term).Replace(@"\ ", @"\s+") + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
