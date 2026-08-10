namespace CardiTrack.Domain.Enums;

/// <summary>
/// Who a daily digest is written for. The framing changes with the reader — plain reassuring
/// language for family, first-person encouragement for the wearer (docs/llm_design.md prompt
/// variants) — so each audience is its own generated text, never a re-badged copy.
/// </summary>
public enum DigestAudience
{
    /// <summary>Family caregivers — the app's primary readers.</summary>
    Family = 1,

    /// <summary>The wearer themselves — generated only once wearer logins exist.</summary>
    Wearer = 2,
}
