namespace DmarcAnalyzer.Api.Application.Security;

public static class CredentialProtectionExtensions
{
    public const string KeyConfigPath = "Security:CredentialEncryptionKey";

    public static IServiceCollection AddCredentialProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration[KeyConfigPath];

        if (string.IsNullOrWhiteSpace(key))
        {
            services.AddSingleton<ICredentialProtector>(sp =>
            {
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(CredentialProtectionExtensions))
                    .LogWarning(
                        "{ConfigPath} is not configured; mailbox credentials will be stored in plaintext. " +
                        "Generate a key with: openssl rand -base64 32",
                        KeyConfigPath);
                return new NullCredentialProtector();
            });
        }
        else
        {
            services.AddSingleton<ICredentialProtector>(new AesGcmCredentialProtector(key));
        }

        return services;
    }
}

/// <summary>Passthrough used when no encryption key is configured (dev fallback).</summary>
public sealed class NullCredentialProtector : ICredentialProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string stored) => stored;
    public bool IsProtected(string stored) => false;
}
