using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardiTrack.Application.Services;

/// <summary>
/// How the measured movements behind an insight are stored on the row and read back.
/// </summary>
/// <remarks>
/// <para>
/// <c>MemberInsight.KeyFindings</c> is newline-joined text on the stated grounds that it is an
/// ordered list of sentences with no structure inside it. That reasoning still holds for the
/// sentences; it does not describe this. A movement is six measured fields — which metric, in what
/// unit, where it sits, what is usual, how far apart, over how many days — and flattening that
/// into prose would mean a client parsing figures back out of a sentence a model wrote.
/// </para>
/// <para>
/// Only what was measured is stored. How a movement is <em>graded</em> and how that grade is
/// worded both live in <see cref="MetricValence"/> and are applied on the way out, so revising the
/// wording of a grade reaches every caregiver on their next request rather than waiting for a
/// regeneration pass to rewrite rows.
/// </para>
/// </remarks>
public static class InsightMovements
{
    /// <summary>
    /// Enum members by name, so a row survives the list being reordered or renumbered, and
    /// indented-free so the column stays compact.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The movements as the column carries them, or null where nothing moved — null rather than
    /// "[]" so an empty list and an unwritten column read the same on the way back.
    /// </summary>
    public static string? Write(IReadOnlyList<MetricMovement>? movements) =>
        movements is null || movements.Count == 0
            ? null
            : JsonSerializer.Serialize(movements, Format);

    /// <summary>
    /// What the column holds, or nothing at all if it cannot be read.
    /// </summary>
    /// <remarks>
    /// A row written by a build that knew a metric this one does not would otherwise throw on
    /// every read of that member's dashboard. The movements are a supplement to an insight that
    /// still has its summary and its findings, so the dashboard is better served without them than
    /// failed outright; the next pass overwrites the row either way.
    /// </remarks>
    public static IReadOnlyList<MetricMovement> Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<MetricMovement>>(stored, Format) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
