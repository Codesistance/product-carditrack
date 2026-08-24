namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// Content that may reach only the in-estate clinical slot (<c>AI:Private</c>) — the member
/// context, fetched readings, notes and questionnaire material DPIA row A20 keeps off the
/// external rewrite provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>The enforcement is this type, not a test.</b> The payload is private: the only way out is
/// <see cref="RenderForClinicalPrompt"/>, whose name states the one destination, and no
/// rewrite-slot prompt builder takes this type. Passing it to one is a compile error; string
/// interpolation — the accidental path — hits the poisoned <see cref="ToString"/> and yields a
/// marker instead of data. Before this, the boundary was held by which method happened to receive
/// which local variable, a convention the security review scored as no control at all once the
/// data vocabulary widened.
/// </para>
/// <para>
/// The slot-guard test (<c>SlotBoundaryTests</c>) asserts the two properties the compiler cannot:
/// that no public member other than the render method exposes the payload, and that
/// <see cref="ToString"/> leaks nothing.
/// </para>
/// </remarks>
public sealed class ClinicalOnlyData
{
    private readonly string _content;

    private ClinicalOnlyData(string content) => _content = content;

    /// <summary>Wraps prompt sections destined for the clinical slot. The caller composes them;
    /// this type's job is to make sure nothing else reads them back.</summary>
    public static ClinicalOnlyData Wrap(string content) => new(content);

    /// <summary>The one legitimate exit: the clinical (Private-slot) prompt being assembled.</summary>
    public string RenderForClinicalPrompt() => _content;

    /// <summary>Poisoned on purpose — interpolating this type into any other prompt yields a
    /// marker, not member data.</summary>
    public override string ToString() => "[clinical-only data — not for this prompt]";
}

/// <summary>
/// The de-identified product of the clinical read — what the external rewrite slot is allowed to
/// see. Name discipline is the constructor's contract: the text must already carry the
/// <c>CardiTrackCardiMember</c> placeholder, never the member's real name.
/// </summary>
/// <remarks>
/// Counterpart to <see cref="ClinicalOnlyData"/>: rewrite-slot prompt builders take this type and
/// not that one, which is the compile-time half of DPIA row A20's boundary. This type is
/// deliberately transparent — findings are meant to be read — because the protection is in what
/// the rewrite prompt's signature <em>refuses</em>, not in hiding the findings from the code that
/// owns them.
/// </remarks>
public sealed record DeidentifiedFindings(string Text)
{
    public override string ToString() => Text;
}
