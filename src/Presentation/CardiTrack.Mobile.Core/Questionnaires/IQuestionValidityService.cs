using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Questionnaires;

/// <summary>
/// Checks a question the app is about to show against its validity, and tells the server about the
/// ones that have lapsed.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, deliberately one call. The screens need an answer now — draw this card or do not — and
/// the platform needs the lapsed row retired, because a question left <c>Pending</c> goes on
/// blocking every future question for that member. Splitting them would leave every caller
/// responsible for remembering the second half, and the caller that forgot would be the one that
/// left a family gagged.
/// </para>
/// <para>
/// The verdict is the app's; the write is a request. The server re-checks against its own clock
/// before retiring anything, and a background sweep catches the members whose family never opened
/// the app at all — so this is the fast path, not the guarantee.
/// </para>
/// </remarks>
public interface IQuestionValidityService
{
    /// <summary>
    /// The pending question worth showing, or null when the one supplied has outlived the moment it
    /// asked about. Retiring the lapsed one on the server is started but never waited on: the caller
    /// is drawing a screen, and whether the row has been tidied up yet changes nothing it draws.
    /// </summary>
    /// <param name="pending">
    /// The pending question the API returned, or null when it returned none. Null in, null out —
    /// callers pass <c>result.Pending</c> straight through rather than checking twice.
    /// </param>
    QuestionnaireResponse? Verify(QuestionnaireResponse? pending);
}
