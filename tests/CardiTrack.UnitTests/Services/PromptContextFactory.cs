using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Builds the member-context composer the prompt services take, for tests that are about the
/// service rather than about the context.
/// </summary>
/// <remarks>
/// A real composer over real sources, not a substitute: these tests assert on the prompt text the
/// service produces, and a stubbed composer would let the member block drift away from what
/// production sends without a single test noticing. The sources read through the substituted
/// <see cref="IUnitOfWork"/> the test already arranges, so a test that sets up no questionnaires and
/// no environmental readings gets a prompt with neither section — which is the common case in
/// production too.
/// </remarks>
internal static class PromptContextFactory
{
    /// <summary>
    /// A real encryption service over a fixed test key. Real rather than substituted because the
    /// caregiver-note path is one of the things these prompts are asserted on, and its fallback for
    /// values written before encryption is what lets a test arrange a plain-text note and still read
    /// it back in the prompt.
    /// </summary>
    internal static IEncryptionService Encryption { get; } =
        new AesEncryptionService(Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));

    /// <summary>The full production source set, wired over this test's substitutes.</summary>
    internal static MemberContextComposer Composer(IUnitOfWork unitOfWork) =>
        Composer(unitOfWork, Encryption);

    internal static MemberContextComposer Composer(IUnitOfWork unitOfWork, IEncryptionService encryption) =>
        new(
            [
                new DemographicsContextSource(encryption),
                new EnvironmentalContextSource(unitOfWork),
                new MonitoringContextSource(unitOfWork),
                new QuestionnaireAnswersContextSource(unitOfWork, encryption),
            ],
            NullLogger<MemberContextComposer>.Instance);
}
