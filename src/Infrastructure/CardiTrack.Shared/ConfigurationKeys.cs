namespace CardiTrack.Shared;

public static class ConfigurationKeys
{
    public static class ConnectionStrings
    {
        public const string DefaultConnection = "ConnectionStrings:DefaultConnection";
        public const string Redis = "ConnectionStrings:Redis";
    }

    public static class Auth0
    {
        public const string Domain = "Auth0:Domain";
        public const string Audience = "Auth0:Audience";
        public const string ClientId = "Auth0:ClientId";
        public const string ClientSecret = "Auth0:ClientSecret";
        public const string CallbackUrl = "Auth0:CallbackUrl";
        public const string LogoutUrl = "Auth0:LogoutUrl";
    }

    public static class Cors
    {
        public const string AllowedOrigins = "Cors:AllowedOrigins";
    }

    public static class Serilog
    {
        public const string SeqUrl = "Serilog:SeqUrl";
    }

    public static class Apm
    {
        /// <summary>
        /// Selector for APM provider injection — the value names an engine in
        /// ApmProviderRegistry (e.g. "BetterStack"); empty disables shipping.
        /// Read via ApmExtensions.LoadEngine. Apm:Data stays options-bound (it may
        /// arrive as one JSON env var): see ApmExtensions.GetApmOptions.
        /// </summary>
        public const string Engine = "Apm:Engine";
    }

    public static class Encryption
    {
        public const string Key = "Encryption:Key";
        public const string IV = "Encryption:IV";
    }

    public static class Health
    {
        public const string Token = "Health:Token";
    }

    public static class CloudRun
    {
        /// <summary>Bare env var injected by Cloud Run (no section) — the port to listen on.</summary>
        public const string Port = "PORT";
    }

    /// <summary>Section — options-bound via IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
    public static class IpRateLimiting
    {
        public const string SectionName = "IpRateLimiting";
    }

    public static class Workers
    {
        public static class WearableSyncWorker
        {
            public const string CronExpression = "Workers:WearableSyncWorker:CronExpression";
        }
    }

    public static class Api
    {
        public const string BaseUrl = "Api:BaseUrl";
    }

    /// <summary>Array section — use with IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
    public static class DeviceProviders
    {
        public const string SectionName = "DeviceProviders";
    }

    public static class AI
    {
        /// <summary>Name of the active provider for generative tasks (reports, chat). E.g. "Gemini"</summary>
        public const string GeneralProvider = "AI:GeneralProvider";

        /// <summary>Name of the active provider for medical analysis tasks. E.g. "MedGemma"</summary>
        public const string MedicalProvider = "AI:MedicalProvider";

        /// <summary>Array section — use with IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
        public const string ProvidersSectionName = "AI:Providers";
    }
}
