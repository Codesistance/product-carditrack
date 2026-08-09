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
        ["AI:Private:TimeoutSeconds"] = "300"
    };

    private static ServiceProvider Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddAiServices(configuration).BuildServiceProvider();
    }
}
