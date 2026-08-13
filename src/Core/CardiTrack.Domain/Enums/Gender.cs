using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Biological sex, which is what the clinical paths actually read this for: age and sex are what
/// shift the reference range a heart rate or a sleep duration should be read against.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="Male"/> and <see cref="Female"/> are offered at capture. <c>Other = 3</c> was
/// removed once the onboarding form began asking, because it named a gender identity the clinical
/// reading cannot use while occupying the slot where a usable answer would go — the members who
/// held it were migrated to <see cref="PreferNotToSay"/>.
/// </para>
/// <para>
/// <see cref="PreferNotToSay"/> stays, and is deliberately not offered in the picker. It is the
/// resting state of every member created before the form asked, and the prompt layer renders it as
/// "not stated" rather than dropping the line — an absent answer and an unasked question look the
/// same to the model otherwise. Values are non-contiguous for that reason: 3 is retired, not free.
/// </para>
/// </remarks>
public enum Gender
{
    [Display(Name = "Male")]
    Male = 1,

    [Display(Name = "Female")]
    Female = 2,

    [Display(Name = "Prefer Not to Say")]
    PreferNotToSay = 4
}
