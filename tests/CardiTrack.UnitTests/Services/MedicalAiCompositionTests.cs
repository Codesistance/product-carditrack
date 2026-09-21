using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// What a MedGemma-calling host gets from <c>AddMedicalAiServices</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline takes this method and not <c>AddAiServices</c> — it holds no public-provider key
/// — so anything the batch passes need has to be reachable from here. <c>IHealthInsightService</c>
/// was registered in the public method instead, and nothing said so: the pipeline's optional
/// dependencies simply stayed null, the assess and digest passes skipped their generation step in
/// silence, and every insight endpoint would have served empty text forever while looking
/// perfectly healthy.
/// </para>
/// <para>
/// A resolution test rather than a registration one: asking the container to actually build each
/// service is what catches a missing transitive dependency, which is the half of that failure a
/// "was it registered" assertion would have missed.
/// </para>
/// </remarks>
public class MedicalAiCompositionTests
{
    [Theory]
    [InlineData(typeof(IHealthInsightService))]
    [InlineData(typeof(StatusLineGenerationService))]
    [InlineData(typeof(AdviseGenerationService))]
    [InlineData(typeof(TrendInterpretationService))]
    public void EveryBatchWriterCanBeBuiltFromTheMedicalSlotAlone(Type service)
    {
        using var provider = BuildPipelineLikeProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(service));
    }

    /// <summary>
    /// The pipeline's composition root, reduced to what it takes from shared code: the medical
    /// slot plus the persistence and encryption ports every host registers for itself.
    /// </summary>
    private static ServiceProvider BuildPipelineLikeProvider()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Private:BaseUrl"] = "http://localhost:11434",
                ["Ai:Private:Model"] = "medgemma",
                ["Ai:Rewrite:Kind"] = "Ollama",
                ["Ai:Rewrite:BaseUrl"] = "http://localhost:11434",
                ["Ai:Rewrite:Model"] = "medgemma",
            })
            .Build();

        services.AddLogging();
        services.AddMedicalAiServices(configuration);

        // The ports each host supplies. Substituted rather than wired to a database: what is under
        // test is whether the graph closes, not what the rows say.
        //
        // The DbContext is registered rather than substituted, because it cannot be: it is a
        // class, not a port, and IMemberWriteGuard takes it directly — taking a row lock is not
        // something a repository contract can express. No connection is opened here; the graph is
        // validated at build, and nothing in this test resolves as far as a query.
        services.AddDbContext<CardiTrack.Infrastructure.Persistence.CardiTrackDbContext>(
            options => options.UseNpgsql("Host=localhost;Database=unused"));
        services.AddScoped(_ => Substitute.For<IUnitOfWork>());
        services.AddScoped(_ => Substitute.For<CardiTrack.Application.Interfaces.Security.IEncryptionService>());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Both on: a dependency this host cannot build should fail here rather than at 3am in
            // a Cloud Run job, and a scoped service captured by a singleton is the other way a
            // composition root goes wrong quietly.
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
