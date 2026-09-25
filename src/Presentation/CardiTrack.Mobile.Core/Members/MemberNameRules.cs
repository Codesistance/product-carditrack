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

    /// <summary>The last name as the request should carry it: trimmed, or null for none.</summary>
    public static string? LastNameOrNull(string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();
}
