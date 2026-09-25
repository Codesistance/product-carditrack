using CardiTrack.Domain.Common;

namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// The add and edit forms' name rules, word for word the API's (<c>CreateCardiMemberValidator</c>,
/// <c>UpdateCardiMemberValidator</c>), so a caregiver is stopped on the field rather than by a
/// server error after tapping save.
/// </summary>
public static class MemberNameRules
{
    public const int MaxLength = 100;

    /// <summary>Why this first name would be refused, or null when it is fine.</summary>
    public static string? FirstNameError(string? firstName)
    {
        var trimmed = firstName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "First name is required";
        return trimmed.Length > MaxLength ? $"First name cannot exceed {MaxLength} characters" : null;
    }

    /// <summary>Why this last name would be refused, or null when it is fine — including when it is empty.</summary>
    public static string? LastNameError(string? lastName) =>
        (lastName?.Trim().Length ?? 0) > MaxLength ? $"Last name cannot exceed {MaxLength} characters" : null;

    /// <summary>The shortest and longest single name an API from before the first/last split accepts.</summary>
    public const int LegacyMinLength = 2;
    public const int LegacyMaxLength = 100;

    /// <summary>
    /// The single <c>name</c> the app restates beside the two parts, so a save still works against
    /// an API from before the split — held to that API's 2–100 rule, which the parts alone are not.
    /// </summary>
    /// <remarks>
    /// Over 100 characters, the joined name is cut to 100 (and trailing space trimmed): an old API
    /// stores a shortened name rather than refusing the save. A one-letter single name gets a
    /// full stop ("A."), how an initial is written, rather than failing the old minimum. A current
    /// API ignores this field whenever <c>firstName</c> is sent, so neither ever reaches it.
    /// </remarks>
    public static string LegacyName(string firstName, string? lastName)
    {
        var full = PersonName.Join(firstName.Trim(), LastNameOrNull(lastName));
        if (full.Length > LegacyMaxLength)
            full = full[..LegacyMaxLength].TrimEnd();
        return full.Length < LegacyMinLength ? full + "." : full;
    }

    /// <summary>The last name as the request should carry it: trimmed, or null for none.</summary>
    public static string? LastNameOrNull(string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();
}
