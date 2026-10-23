using Amazon.RDS.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DmarcAnalyzer.Api.Data;

/// <summary>
/// Builds the one <see cref="NpgsqlDataSource"/> a process connects through, so
/// every mode authenticates the same way.
/// <para>
/// With <c>Database:IamAuth</c> on, the password is an RDS IAM authentication
/// token instead of a stored secret. A token is only valid for 15 minutes, so a
/// fixed connection string cannot carry one: a fresh token is generated each time
/// Npgsql opens a physical connection. Connections already open stay open — the
/// token is only checked at login.
/// </para>
/// <para>
/// IAM mode also verifies the server, because the token is a live credential and
/// <c>sslmode=require</c> would hand it to whatever answers on the port. Which
/// root to verify against is deliberately <em>not</em> decided here: an RDS or
/// Aurora endpoint presents a certificate from the Amazon RDS CA, which no
/// system trust store carries, while an RDS Proxy endpoint presents one from
/// Amazon Trust Services, which every system store already has. The operator
/// says which with the standard Postgres mechanisms Npgsql honours on its own —
/// <c>Root Certificate=</c> in the connection string, the <c>PGSSLROOTCERT</c>
/// environment variable, or nothing for the system store. See
/// <c>docs/ops/aws-rds-iam.md</c>.
/// </para>
/// </summary>
public static class DatabaseDataSource
{
    public const string IamAuthKey = "Database:IamAuth";

    /// <summary>
    /// The environment variable libpq and Npgsql both read for the CA file when
    /// the connection string names none.
    /// </summary>
    public const string RootCertificateVariable = "PGSSLROOTCERT";

    /// <summary>Builds the data source; with IAM auth on, configures and validates it.</summary>
    public static NpgsqlDataSource Create(IConfiguration configuration, string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        if (configuration.GetValue<bool>(IamAuthKey))
        {
            UseIamAuth(builder);
        }

        return builder.Build();
    }

    /// <summary>
    /// One line at startup saying how IAM mode will verify the database server,
    /// because the wrong root fails as a bare TLS chain error on the first
    /// connection, which does not say which of the three places Npgsql looked.
    /// Silent when IAM auth is off.
    /// </summary>
    public static void LogDatabaseAuthStatus(
        this ILogger logger, IConfiguration configuration, NpgsqlDataSource dataSource)
    {
        if (!configuration.GetValue<bool>(IamAuthKey))
        {
            return;
        }

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        logger.LogInformation(
            "Database IAM auth is on: per-connection RDS tokens for {Username}@{Host}:{Port}, " +
            "server certificate verified against {TrustStore}.",
            settings.Username, settings.Host, settings.Port, DescribeTrustStore(settings));
    }

    /// <summary>
    /// Where Npgsql will look for the CA, in the order it looks: the connection
    /// string, <c>PGSSLROOTCERT</c>, <c>~/.postgresql/root.crt</c> if present, and
    /// otherwise the system trust store. Mirrors Npgsql's own lookup so the log
    /// line cannot drift from what the connector does.
    /// </summary>
    public static string DescribeTrustStore(NpgsqlConnectionStringBuilder settings)
        => DescribeTrustStore(
            settings,
            Environment.GetEnvironmentVariable(RootCertificateVariable),
            DefaultRootCertificatePath());

    public static string DescribeTrustStore(
        NpgsqlConnectionStringBuilder settings, string? rootCertificateVariable, string? defaultRootCertificate)
    {
        if (!string.IsNullOrEmpty(settings.RootCertificate))
        {
            return $"{settings.RootCertificate} (Root Certificate in the connection string)";
        }

        if (!string.IsNullOrEmpty(rootCertificateVariable))
        {
            return $"{rootCertificateVariable} ({RootCertificateVariable})";
        }

        if (defaultRootCertificate is not null)
        {
            return $"{defaultRootCertificate} (libpq default location)";
        }

        return "the system trust store (no Root Certificate, no " + RootCertificateVariable + ")";
    }

    private static string? DefaultRootCertificatePath()
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "APPDATA" : "HOME");
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        var path = Path.Combine(home, OperatingSystem.IsWindows() ? "postgresql" : ".postgresql", "root.crt");
        return File.Exists(path) ? path : null;
    }

    private static void UseIamAuth(NpgsqlDataSourceBuilder builder)
    {
        var settings = builder.ConnectionStringBuilder;

        if (string.IsNullOrEmpty(settings.Host) || string.IsNullOrEmpty(settings.Username))
        {
            throw new InvalidOperationException(
                "Database__IamAuth needs a host and username in the connection string: the " +
                "IAM token is signed for that exact host, port and database user.");
        }

        // Npgsql refuses a password alongside a password provider, but its message
        // does not say which setting to change.
        if (!string.IsNullOrEmpty(settings.Password))
        {
            throw new InvalidOperationException(
                "Database__IamAuth is on but the connection string contains a password. " +
                "Remove the password or disable IAM auth.");
        }

        // RDS rejects IAM logins over unencrypted connections.
        if (settings.SslMode is SslMode.Disable)
        {
            throw new InvalidOperationException(
                "Database__IamAuth requires TLS, but the connection string sets sslmode to disable. " +
                "RDS only accepts IAM logins over TLS.");
        }

        // `require` encrypts but accepts any certificate, so a host impersonating
        // the database would receive a token that is valid for 15 minutes.
        // VerifyCA is left alone: it is a deliberate, stricter-than-require choice.
        if (settings.SslMode is SslMode.Allow or SslMode.Prefer or SslMode.Require)
        {
            settings.SslMode = SslMode.VerifyFull;
        }

        // Called as each connection opens, with the settings of the host that is
        // being opened: the token is signed for one host and port, and with
        // several hosts in the connection string each host's pool passes its own.
        //
        // Generating per connection is cheap: signing is local, and the SDK keeps
        // the credentials from its ambient chain (the ECS task role, an instance
        // profile, or IRSA - the same chain the S3 source uses) until they near
        // expiry, so only a credential refresh reaches the network. The region
        // comes from that same chain (AWS_REGION and friends), not from the
        // hostname, so it has to be the database's region.
        builder.UsePasswordProvider(
            hostSettings => RDSAuthTokenGenerator.GenerateAuthToken(
                hostSettings.Host!, hostSettings.Port, hostSettings.Username!),
            async (hostSettings, _) => await RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                hostSettings.Host!, hostSettings.Port, hostSettings.Username!));
    }
}
