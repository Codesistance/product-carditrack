using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// An <see cref="AdviseGenerationService"/> that does nothing: it runs over its own substitute
/// unit of work whose member lookup answers null, so <c>RegenerateIfDueAsync</c> returns at the
/// guard without a model call or a write. Digest-suite tests use it so their assertions — several
/// of which read <c>_medicalAi.ReceivedCalls().Single()</c> — see only the digest's own calls; the
/// generator has its own suite (<see cref="AdviseGenerationServiceTests"/>).
/// </summary>
internal static class InertAdviseGenerator
{
    public static AdviseGenerationService Create()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        return new AdviseGenerationService(
            unitOfWork,
            Substitute.For<IMedicalAiService>(),
            PromptContextFactory.Composer(unitOfWork),
            NullLogger<AdviseGenerationService>.Instance);
    }
}
