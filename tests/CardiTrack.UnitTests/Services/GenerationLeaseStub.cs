using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The once-per-period claim, as the fixtures that are not about it need it to behave.
/// </summary>
/// <remarks>
/// Every generator that writes a period now takes a claim before it calls the model, and
/// NSubstitute's auto-substitute answers an unconfigured <c>TryClaimAsync</c> with <c>false</c> —
/// so without this a fixture testing what a Weekbook <em>says</em> would silently be testing what
/// happens when another execution got there first, and pass for the wrong reason. Granting it
/// explicitly is also the honest reading of those fixtures: their subject is the generation, and
/// the claim is a precondition of reaching it.
/// </remarks>
internal static class GenerationLeaseStub
{
    /// <summary>Grants every claim on <paramref name="unitOfWork"/>.</summary>
    internal static void GrantAll(IUnitOfWork unitOfWork) =>
        unitOfWork.GenerationLeases
            .TryClaimAsync(
                Arg.Any<Guid>(), Arg.Any<GenerationWork>(), Arg.Any<DateOnly>(),
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
}
