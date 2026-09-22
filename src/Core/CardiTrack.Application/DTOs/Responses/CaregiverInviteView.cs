namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What the invitation's landing page is allowed to say before anybody has signed in.
/// </summary>
/// <remarks>
/// <para>
/// An explicit ceiling rather than a convenience, for the same reason
/// <see cref="WearerInviteView"/> is one: the page is served to whoever holds the link, which in
/// the worst case is not the person it was sent to. So this type names, in one place, the complete
/// set of facts a stranger holding the token could learn — two first names and a deadline. No
/// surname, no email, no date of birth, no reading, no alert, no other member of the family, and
/// nothing about what CardiTrack has ever observed about anyone.
/// </para>
/// <para>
/// The role and the grants are deliberately absent too. They are what the invitee is agreeing to,
/// but they are only meaningful once there is an account to attach them to, and naming them here
/// would tell a stranger how much access the token is worth.
/// </para>
/// </remarks>
/// <param name="MemberFirstName">The person being watched, as the page names them.</param>
/// <param name="InviterFirstName">Who is asking.</param>
/// <param name="ExpiresAt">When the invitation stops working.</param>
public record CaregiverInviteView(
    string MemberFirstName,
    string InviterFirstName,
    DateTime ExpiresAt);
