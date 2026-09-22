namespace CardiTrack.Application.Exceptions;

/// <summary>
/// A family rule refused an otherwise well-formed request — the caller may do this sort of thing,
/// but not this particular one, and the reason is worth naming.
/// </summary>
/// <remarks>
/// Separate from the <see cref="KeyNotFoundException"/> the access checks throw, and deliberately
/// so: that one is opaque on purpose, because telling an outsider "you are not an admin of this
/// family" confirms the family exists. These are refusals for somebody who is already inside, where
/// vagueness would just be unhelpful — "you are the only admin" is exactly what they need to hear.
/// </remarks>
public class FamilyRuleException : Exception
{
    /// <summary>The family would be left with nobody able to invite, approve, or pay for it.</summary>
    public const string CannotDemoteLastAdmin = "CANNOT_DEMOTE_LAST_ADMIN";

    /// <summary>An admin tried to leave without handing the family on.</summary>
    public const string AdminMustTransferFirst = "ADMIN_MUST_TRANSFER_FIRST";

    /// <summary>Leaving a family you are alone in is closing your account, and has its own flow.</summary>
    public const string UseAccountDeletion = "USE_ACCOUNT_DELETION";

    /// <summary>The plan this family is on has no room for another person.</summary>
    public const string MemberLimitReached = "MEMBER_LIMIT_REACHED";

    /// <summary>The plan this family is on has no room for another CardiMember.</summary>
    public const string CardiMemberLimitReached = "CARDIMEMBER_LIMIT_REACHED";

    public string Code { get; }

    public FamilyRuleException(string code, string message) : base(message)
    {
        Code = code;
    }
}
