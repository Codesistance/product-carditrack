namespace CardiTrack.Domain.Extensions;

public static class DateOnlyExtensions
{
    /// <summary>
    /// Completed years between a date of birth and <paramref name="asOf"/> — the member's age as a
    /// person would state it, not a rounded fraction of a year.
    /// </summary>
    public static int ToAgeInYears(this DateOnly dateOfBirth, DateOnly asOf)
    {
        var age = asOf.Year - dateOfBirth.Year;
        if (asOf < dateOfBirth.AddYears(age))
            age--;
        return age;
    }
}
