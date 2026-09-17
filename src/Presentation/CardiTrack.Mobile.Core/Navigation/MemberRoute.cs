namespace CardiTrack.Mobile.Core.Navigation;

/// <summary>
/// The CardiMember id a pushed page was opened for, as Shell hands it over, and whether a load
/// that already ran without it is owed again.
/// </summary>
/// <remarks>
/// <para>
/// Shell sets a page's <c>[QueryProperty]</c> setters and raises <c>OnAppearing</c> as two
/// separate steps, and a page does not get to choose the order. A page that starts its load from
/// OnAppearing can therefore find the id still unset and ask the API about
/// <c>00000000-0000-0000-0000-000000000000</c> — which every CardiMember endpoint answers with
/// the deliberately vague "CardiMember not found", because no caregiver is linked to the empty
/// id and the API will not distinguish "not yours" from "no such member".
/// </para>
/// <para>
/// The member detail page met this live and grew a guard and a thirty-second tick of its own
/// (dev, 2026-08-20), so it heals itself within half a minute. The settings pages have neither a
/// tick nor a Try again button: there, the refusal was the whole screen, permanently, for a
/// member the caregiver had just been reading.
/// </para>
/// <para>
/// So the id is not read once and trusted. A load that finds none says so through
/// <see cref="LoadedWithoutId"/> and shows the page's own "whose page is this" message instead
/// of spending a round trip on an id that cannot match anything; if the id then arrives,
/// <see cref="Accept"/> says the load is owed. Correct whichever way round the two land, and
/// without depending on an ordering the framework does not promise.
/// </para>
/// </remarks>
public sealed class MemberRoute
{
    private bool _loadedWithoutId;

    /// <summary>The id the page was opened for, or <see cref="Guid.Empty"/> until one arrives.</summary>
    public Guid Id { get; private set; }

    /// <summary>True while the page has nothing to load anything for.</summary>
    public bool IsMissing => Id == Guid.Empty;

    /// <summary>
    /// Takes the raw query-property value Shell passes: percent-encoded, and null or unparseable
    /// on a navigation that could not carry an id.
    /// </summary>
    /// <returns>
    /// True when this arrival leaves a load owed — the value is a usable id, it is not the one
    /// the page already had, and a load has already run without it. False otherwise, including
    /// the ordinary case where the id lands before the page ever tries to load.
    /// </returns>
    public bool Accept(string? raw)
    {
        var id = Guid.TryParse(Uri.UnescapeDataString(raw ?? string.Empty), out var parsed)
            ? parsed
            : Guid.Empty;

        // An unusable value leaves the page exactly as it was. Overwriting a good id with
        // Guid.Empty would take a working page down, and a repeat of the id already held is
        // not an arrival worth reloading for.
        if (id == Guid.Empty || id == Id)
            return false;

        Id = id;

        var owed = _loadedWithoutId;
        _loadedWithoutId = false;
        return owed;
    }

    /// <summary>
    /// Records that a load ran with no id behind it, so the arrival that follows knows to run it
    /// again. The page calls this instead of making the request.
    /// </summary>
    public void LoadedWithoutId() => _loadedWithoutId = true;

    /// <summary>
    /// What a page shows in place of its data when it was opened without an id. Deliberately not
    /// the API's "CardiMember not found": that is the server refusing an id, and this is the app
    /// never having had one — a caregiver told the first would go looking for a member that is
    /// still perfectly fine.
    /// </summary>
    public const string MissingMessage = "We couldn't tell whose page this is — go back and try again.";
}
