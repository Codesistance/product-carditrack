namespace CardiTrack.Domain.Common;

/// <summary>
/// The one rule for turning a single full name into first and last parts, and back.
/// </summary>
/// <remarks>
/// <see cref="Split"/> mirrors the <c>SplitCardiMemberName</c> migration's SQL backfill exactly —
/// first whitespace-delimited token is the first name, the rest (trimmed) is the last name, null
/// when there is no rest — so a member backfilled by the migration and one created today by an
/// older app build still sending a single <c>Name</c> come out the same.
/// </remarks>
public static class PersonName
{
    /// <summary>
    /// The whitespace the split recognises — deliberately this explicit set rather than
    /// <see cref="char.IsWhiteSpace(char)"/>, because it is what the migration's SQL can match too.
    /// </summary>
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    /// <summary>Splits a full name at its first run of whitespace.</summary>
    /// <returns>
    /// The first token, and the trimmed remainder or null when there is none. A blank input gives
    /// an empty first name, which validation then rejects as it would a blank <c>FirstName</c>.
    /// </returns>
    public static (string FirstName, string? LastName) Split(string? fullName)
    {
        var trimmed = fullName?.Trim(Separators) ?? string.Empty;
        var cut = trimmed.IndexOfAny(Separators);
        if (cut < 0)
            return (trimmed, null);

        var rest = trimmed[(cut + 1)..].Trim(Separators);
        return (trimmed[..cut], rest.Length == 0 ? null : rest);
    }

    /// <summary>First and last name joined by a single space; the first name alone when there is no last.</summary>
    public static string Join(string firstName, string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? firstName : $"{firstName} {lastName}";
}
