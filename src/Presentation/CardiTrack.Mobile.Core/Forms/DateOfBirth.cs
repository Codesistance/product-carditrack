namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// The rule a CardiMember's date of birth has to meet before a form sends it, kept out of the
/// pages so the two forms that ask for one — add and edit — apply the same rule and can be
/// pinned without a MAUI host.
/// </summary>
/// <remarks>
/// A date of birth is required. The forms used to stand in today's date for one that was never
/// chosen, which created a member born this morning without anyone deciding that; with the field
/// showing a placeholder when unset, an unset date is now a refusal with a message, never a
/// substitute. The age band is the server's — see <c>HealthReferenceRanges</c> — so a date the
/// server would refuse is refused here first, with the same words the edit form already used.
/// </remarks>
public static class DateOfBirth
{
    public const int MinimumAge = 18;
    public const int MaximumAge = 120;

    /// <summary>Shown under the field when no date has been chosen.</summary>
    public const string MissingMessage = "Choose their date of birth";

    /// <summary>Shown under the field when the date puts them outside the age band.</summary>
    public const string AgeMessage = "CardiMember must be between 18 and 120 years old";

    /// <summary>What is wrong with <paramref name="chosen"/> as a date of birth on <paramref name="today"/>, or null.</summary>
    public static string? Validate(DateTime? chosen, DateOnly today)
    {
        if (chosen is not { } date)
            return MissingMessage;

        var age = AgeOn(DateOnly.FromDateTime(date), today);
        return age < MinimumAge || age > MaximumAge ? AgeMessage : null;
    }

    /// <summary>Whole years lived by <paramref name="today"/>; the birthday itself counts.</summary>
    public static int AgeOn(DateOnly dateOfBirth, DateOnly today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (today.Month < dateOfBirth.Month || (today.Month == dateOfBirth.Month && today.Day < dateOfBirth.Day))
            age--;
        return age;
    }
}
