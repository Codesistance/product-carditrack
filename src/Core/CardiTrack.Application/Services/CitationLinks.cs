namespace CardiTrack.Application.Services;

/// <summary>
/// The URL behind a quoted citation line — the union of the two closed citation sets a chat
/// reply can quote (<see cref="WellnessGuidelines"/> for advise, <see cref="ChatDataRegistry.Bands"/>
/// for inference), keyed by the exact citation text those classes are the only authors of.
/// </summary>
/// <remarks>
/// In Application rather than the mobile client because it is the same kind of thing as the sets
/// it reads: citation policy with no I/O. The client renders a Reference line's authority as a
/// link to what this returns; a line that matches nothing here gets null and renders as plain
/// text — the citation conventions' own rule, one step further: the model never writes the
/// citation, and nothing composes the URL either.
/// </remarks>
public static class CitationLinks
{
    /// <summary>
    /// Where <paramref name="citation"/>'s guidance is published, or null when the line is not one
    /// of the fixed citations — or is one that carries no canonical page (see
    /// <see cref="PublishedBand.Url"/>). Exact text match, because a served reply quotes the fixed
    /// lines verbatim: anything looser would put a link under words the catalogues did not write.
    /// </summary>
    public static string? UrlFor(string citation)
    {
        var trimmed = citation.Trim();
        return WellnessGuidelines.All.FirstOrDefault(r => r.Citation == trimmed)?.Url
            ?? ChatDataRegistry.Bands.FirstOrDefault(b => b.Citation == trimmed)?.Url;
    }
}
