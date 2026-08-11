using CardiTrack.API.Extensions;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// notification_engine.md §7.2 C4 — audience-pinning on the internal enqueue endpoint's GoogleOidc
/// scheme only proves the caller can mint a token for this audience, which any GCP principal in
/// the project can do. The email-pinning check in <c>GoogleOidcExtensions.OnTokenValidated</c> is
/// what actually restricts the caller to the pipeline service account; this is the boundary that
/// decides whether an unauthorized-but-audience-valid caller reaches <c>InternalNotificationsController</c>
/// at all.
/// </summary>
public class InternalEnqueueAuthTests
{
    private const string PipelineServiceAccount = "pipeline-sa@carditrack-490120.iam.gserviceaccount.com";

    [Fact]
    public void RightAudienceRightEmail_Verified_IsAuthorized()
    {
        Assert.True(GoogleOidcExtensions.IsAuthorizedPipelineCaller(
            PipelineServiceAccount, emailVerified: true, PipelineServiceAccount));
    }

    [Fact]
    public void EmailMatchesByCase_StillAuthorized()
    {
        // Google's email claim casing isn't a security boundary — the service account name is.
        Assert.True(GoogleOidcExtensions.IsAuthorizedPipelineCaller(
            PipelineServiceAccount.ToUpperInvariant(), emailVerified: true, PipelineServiceAccount));
    }

    [Fact]
    public void WrongServiceAccountEmail_IsRejected()
    {
        // A different, otherwise-legitimate GCP principal in the same project — audience alone
        // would have let this through.
        Assert.False(GoogleOidcExtensions.IsAuthorizedPipelineCaller(
            "some-other-sa@carditrack-490120.iam.gserviceaccount.com", emailVerified: true, PipelineServiceAccount));
    }

    [Fact]
    public void UnverifiedEmail_IsRejected_EvenIfItMatches()
    {
        // Google issues email_verified=false for domain-unverified accounts — trusting the email
        // claim without this flag would let a spoofable identity in.
        Assert.False(GoogleOidcExtensions.IsAuthorizedPipelineCaller(
            PipelineServiceAccount, emailVerified: false, PipelineServiceAccount));
    }

    [Fact]
    public void MissingEmailClaim_IsRejected()
    {
        Assert.False(GoogleOidcExtensions.IsAuthorizedPipelineCaller(
            null, emailVerified: true, PipelineServiceAccount));
    }
}
