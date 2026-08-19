using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Extensions;

/// <summary>
/// Startup wiring and validation for the two AI systems.
/// </summary>
/// <remarks>
/// Two things are being pinned here. First, that swapping the public provider is a configuration
/// change — if resolving a kind ever needs a code edit, these tests stop compiling. Second, and
/// more important, that the medical slot is <em>not</em> swappable: it must resolve to MedGemma no
/// matter what the public section says, because those prompts carry health data that the DPIA
/// assumes never leaves the project.
/// </remarks>
public class AiServiceExtensionsTests
{
    [Fact]
    public void AddAiServices_Resolves_GeminiForTheGeminiKind()
    {
        var provider = Resolve(Config());

        var client = provider.GetRequiredKeyedService<IExternalAiClient>("GeneralProvider");

        Assert.IsType<GeminiClient>(client);
    }

    [Fact]
    public void AddAiServices_Resolves_AnthropicForTheAnthropicKind()
    {
        var config = Config();
        config["AI:Public:Kind"] = "Anthropic";
        config["AI:Public:Model"] = "claude-opus-5";

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("GeneralProvider");

        Assert.IsType<AnthropicAiClient>(client);
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("Anthropic")]
    public void AddAiServices_PinsTheMedicalProviderToMedGemma_WhateverThePublicProviderIs(string kind)
    {
        var config = Config();
        config["AI:Public:Kind"] = kind;

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("MedicalProvider");

        Assert.IsType<MedGemmaClient>(client);
    }

    // The rewrite slot is the same client type as the medical one — a different model tag on the
    // same in-project host, never a switchable provider (see the class remarks).
    [Fact]
    public void AddAiServices_ResolvesTheRewriteProviderToMedGemmaToo()
    {
        var client = Resolve(Config()).GetRequiredKeyedService<IExternalAiClient>("RewriteProvider");

        Assert.IsType<MedGemmaClient>(client);
    }

    [Fact]
    public void AddAiServices_AppliesThePerKindBaseUrl_WhenNoneIsConfigured()
    {
        var config = Config();
        config["AI:Public:BaseUrl"] = string.Empty;

        var factory = Resolve(config).GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            new Uri("https://generativelanguage.googleapis.com"),
            factory.CreateClient(AiServiceExtensions.PublicHttpClientName).BaseAddress);
    }

    [Fact]
    public void AddAiServices_Throws_WhenThePublicKindIsUnknown()
    {
        var config = Config();
        config["AI:Public:Kind"] = "Llama";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        // The operator sets this through deployment, so the message has to name the variable
        // they actually edit, not the CLR type the binder failed on.
        Assert.Contains("AI__Public__Kind", ex.Message);
        Assert.Contains("Gemini", ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenThePublicKindIsMissing()
    {
        var config = Config();
        config.Remove("AI:Public:Kind");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__Kind", ex.Message);
    }

    [Theory]
    [InlineData("AI:Public:Model", "AI__Public__Model")]
    [InlineData("AI:Public:ApiKey", "AI__Public__ApiKey")]
    [InlineData("AI:Private:Model", "AI__Private__Model")]
    [InlineData("AI:Private:BaseUrl", "AI__Private__BaseUrl")]
    [InlineData("AI:Rewrite:Model", "AI__Rewrite__Model")]
    [InlineData("AI:Rewrite:BaseUrl", "AI__Rewrite__BaseUrl")]
    public void AddAiServices_Throws_WhenARequiredValueIsBlank(string key, string expectedEnvVar)
    {
        var config = Config();
        config[key] = "   ";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains(expectedEnvVar, ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenATimeoutIsNotPositive()
    {
        var config = Config();
        config["AI:Public:TimeoutSeconds"] = "0";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__TimeoutSeconds", ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenABaseUrlIsNotAbsolute()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "localhost:11434";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__BaseUrl", ex.Message);
    }

    // MedGemma authorises callers by IAM, so a Cloud Run BaseUrl without an identity token 403s on
    // every call. Digest and assessment code swallow per-member inference failures, so that 403
    // would read as "nothing was due" rather than an error — the misconfiguration has to be caught
    // at startup or it is invisible.
    [Theory]
    [InlineData("https://carditrack-dev-medgemma-abcdef.a.run.app")]
    [InlineData("https://carditrack-dev-medgemma-abcdef.a.RUN.APP")]
    public void AddAiServices_Throws_WhenACloudRunBaseUrlHasNoIdentityToken(string baseUrl)
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = baseUrl;
        config["AI:Private:UseIdentityToken"] = "false";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__UseIdentityToken", ex.Message);
    }

    [Fact]
    public void AddAiServices_Succeeds_WhenACloudRunBaseUrlHasAnIdentityToken()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Private:UseIdentityToken"] = "true";

        // Registration only — the handler mints a token lazily on first send, so nothing here
        // reaches the metadata server.
        Assert.NotNull(Resolve(config).GetRequiredService<IHttpClientFactory>());
    }

    // The reverse mistake: a bearer credential must never go out over plaintext.
    [Fact]
    public void AddAiServices_Throws_WhenAnIdentityTokenIsUsedOverPlainHttp()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "http://localhost:11434";
        config["AI:Private:UseIdentityToken"] = "true";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__UseIdentityToken", ex.Message);
    }

    // The local default has to stay workable, or every developer turns the check off.
    [Fact]
    public void AddAiServices_Succeeds_ForALocalEndpointWithNoIdentityToken()
    {
        Assert.NotNull(Resolve(Config()).GetRequiredService<IHttpClientFactory>());
    }

    // The rewrite slot validates its own identity-token coherence independently of Private — same
    // rules, same reason (it lives on the same IAM-authorised Cloud Run host in every deployed
    // environment), separate config key.
    [Fact]
    public void AddAiServices_Throws_WhenARewriteCloudRunBaseUrlHasNoIdentityToken()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Rewrite:UseIdentityToken"] = "false";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__UseIdentityToken", ex.Message);
    }

    [Fact]
    public void AddAiServices_Succeeds_WhenARewriteCloudRunBaseUrlHasAnIdentityToken()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Rewrite:UseIdentityToken"] = "true";

        Assert.NotNull(Resolve(config).GetRequiredService<IHttpClientFactory>());
    }

    [Fact]
    public void AddAiServices_Throws_WhenARewriteIdentityTokenIsUsedOverPlainHttp()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "http://localhost:11434";
        config["AI:Rewrite:UseIdentityToken"] = "true";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__UseIdentityToken", ex.Message);
    }

    private static Dictionary<string, string?> Config() => new()
    {
        ["AI:Public:Kind"] = "Gemini",
        ["AI:Public:Model"] = "gemini-2.0-flash",
        ["AI:Public:ApiKey"] = "test-key",
        ["AI:Public:BaseUrl"] = "https://generativelanguage.googleapis.com",
        ["AI:Public:TimeoutSeconds"] = "60",
        ["AI:Public:MaxOutputTokens"] = "16000",
        ["AI:Private:Model"] = "medgemma",
        ["AI:Private:BaseUrl"] = "http://localhost:11434",
        ["AI:Private:TimeoutSeconds"] = "300",
        ["AI:Rewrite:Model"] = "gemma3:4b-it-qat",
        ["AI:Rewrite:BaseUrl"] = "http://localhost:11434",
        ["AI:Rewrite:TimeoutSeconds"] = "300"
    };

    private static ServiceProvider Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        // AddLogging: MedGemmaClient takes an ILogger from the container.
        return new ServiceCollection().AddLogging().AddAiServices(configuration).BuildServiceProvider();
    }
}
