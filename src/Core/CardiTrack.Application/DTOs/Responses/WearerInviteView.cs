namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What the anonymous wearer-facing page is allowed to say. Everything on it is assembled at render
/// time from the invite's own ids, and nothing on it is health data.
/// </summary>
/// <remarks>
/// <para>
/// This exists to be an explicit ceiling rather than a convenience. The page is served to whoever
/// holds the link, which in the worst case is not the wearer at all — a phone left on a table, a
/// message forwarded by mistake. So the type names, in one place, the complete set of facts a
/// stranger holding the token could learn: two first names, a brand, and a deadline. No surname, no
/// email, no date of birth, no reading, no alert, no other member of the family, and no indication
/// of what CardiTrack has ever observed about anyone.
/// </para>
/// <para>
/// First names rather than full ones because the page has to be recognisable to be meaningful — a
/// consent screen that will not say who is asking is not informed consent — and a first name is the
/// least that achieves it.
/// </para>
/// </remarks>
/// <param name="MemberFirstName">The wearer, as the page addresses them.</param>
/// <param name="CaregiverFirstName">Who is asking.</param>
/// <param name="DeviceDisplayName">The brand being authorized, e.g. "Fitbit".</param>
/// <param name="Provider">That brand's wire name, for the form the page posts back.</param>
/// <param name="ExpiresAt">When the invite stops working.</param>
public record WearerInviteView(
    string MemberFirstName,
    string CaregiverFirstName,
    string DeviceDisplayName,
    string Provider,
    DateTime ExpiresAt);
