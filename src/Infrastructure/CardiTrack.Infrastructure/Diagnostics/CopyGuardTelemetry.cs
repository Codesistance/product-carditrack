using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// How often written copy is thrown away by a register guard before anyone reads it, by surface
/// and by which rule rejected it.
/// </summary>
/// <remarks>
/// <para>
/// A guard that discards a reply is doing its job — copy that names a condition, restates its own
/// instructions or would not fit the column must not reach a caregiver. But a guard is also the
/// quietest way for a surface to go blank: the model is called, the tokens are paid for, a Warning
/// is written, and the screen shows nothing. Nothing in the product distinguishes "the readings
/// were unremarkable" from "six replies in a row were rejected for naming a condition", and the
/// second is a prompt problem that will not fix itself.
/// </para>
/// <para>
/// The journal is where this matters most today: its three books have six distinct discard
/// reasons between them, and the standing suspicion about their quality is precisely that the
/// guard is rejecting the informative replies and keeping the vacuous ones. That is a measurable
/// claim, and this is what measures it.
/// </para>
/// <para>
/// Privacy: surface names and reason constants only — never the discarded text, which is model
/// output derived from health data. Same standard as <see cref="AiTelemetry"/>.
/// </para>
/// </remarks>
public static class CopyGuardTelemetry
{
    public static readonly Meter Meter = new(TelemetryNames.PipelineSource);

    public static readonly Counter<long> Discarded = Meter.CreateCounter<long>(
        "carditrack.copy.discarded",
        description: "Model replies thrown away by a register guard, by surface and reason");

    /// <summary>Which surface's copy was discarded, e.g. <c>daybook entry</c>.</summary>
    public const string SurfaceTag = "copy.surface";

    /// <summary>Which guard rejected it — one of the <c>Reason</c> constants below.</summary>
    public const string ReasonTag = "copy.reason";

    /// <summary>Empty, or the model restated its own instructions back.</summary>
    public const string ReasonReadsLikeInstructions = "reads_like_instructions";

    /// <summary>Named a condition or a treatment.</summary>
    public const string ReasonNamesACondition = "names_a_condition";

    /// <summary>Shorter than the minimum the surface requires.</summary>
    public const string ReasonTooFewSentences = "too_few_sentences";

    /// <summary>Used a clinical term without explaining it where it first appears.</summary>
    public const string ReasonUnglossedTerm = "unglossed_term";

    /// <summary>Used the name placeholder with no name on file to resolve it to.</summary>
    public const string ReasonUnresolvablePlaceholder = "unresolvable_placeholder";

    /// <summary>Longer than the column that stores it.</summary>
    public const string ReasonTooLong = "too_long";

    /// <summary>Records one discard.</summary>
    public static void Count(string surface, string reason) =>
        Discarded.Add(
            1,
            new KeyValuePair<string, object?>(SurfaceTag, surface),
            new KeyValuePair<string, object?>(ReasonTag, reason));
}
