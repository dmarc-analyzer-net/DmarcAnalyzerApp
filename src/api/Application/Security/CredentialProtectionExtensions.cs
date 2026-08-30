namespace DmarcAnalyzer.Api.Application.Security;

/// <summary>Registers the <see cref="ICredentialProtector"/> the configuration calls for.</summary>
public static class CredentialProtectionExtensions
{
    /// <summary>Where the master key lives in configuration — also read by backup export to report protection status.</summary>
    public const string KeyConfigPath = "Security:CredentialEncryptionKey";

    /// <summary>AES-GCM when a key is configured; the warning-logging passthrough otherwise.</summary>
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
    /// <inheritdoc />
    public string Protect(string plaintext) => plaintext;

    /// <inheritdoc />
    public string Unprotect(string stored) => stored;

    /// <inheritdoc />
    public bool IsProtected(string stored) => false;
}
