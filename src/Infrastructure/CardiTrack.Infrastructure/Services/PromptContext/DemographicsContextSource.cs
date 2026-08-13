using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// Who the member is, as far as the model needs to know — age, sex, and what the caregiver has
/// written about them. The section every prompt gets.
/// </summary>
/// <remarks>
/// <para>
/// This is where the caregiver's note is decrypted. <c>MedicalNotes</c> is encrypted by
/// <c>CardiMemberService</c> on write, and until this source existed every prompt passed the stored
/// column straight through — so the model was reading the ciphertext envelope
/// (<c>v1:&lt;fingerprint&gt;:&lt;base64&gt;</c>) where the conditions and medication were meant to
/// be. It cost nothing visible: the note simply never informed a single generated word.
/// </para>
/// <para>
/// Decrypting here rather than at each call site is the point of the framework — one reader, one
/// fallback rule, and no way for a fourth prompt to be written that forgets.
/// </para>
/// </remarks>
internal sealed class DemographicsContextSource : IMemberContextSource
{
    /// <summary>
    /// The label the caregiver note sits under. Load-bearing beyond the prompt: the digest's echo
    /// guard watches for "caregiver-reported context" in the model's reply to catch a summary that
    /// restated its instructions, and the instruction blocks scope their never-follow-instructions
    /// warning to this exact phrase. Renaming it breaks both silently.
    /// </summary>
    internal const string CaregiverContextLabel = "Caregiver-reported context";

    private readonly IEncryptionService _encryption;

    public DemographicsContextSource(IEncryptionService encryption) => _encryption = encryption;

    public PromptPurpose Purposes => PromptPurpose.All;

    /// <summary>First. Every other section is read against who this person is.</summary>
    public int Order => 0;

    public Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct)
    {
        var notes = EncryptedFieldReader.Reveal(_encryption, request.Member?.MedicalNotes);

        return Task.FromResult<MemberContextSection?>(
            new MemberContextSection("Member", MedicalPromptBlocks.MemberContext(
                request.Member, request.Today, notes)));
    }
}
