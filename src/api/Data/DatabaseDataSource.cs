using Amazon.RDS.Util;
using Microsoft.Extensions.Configuration;
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
/// </summary>
public static class DatabaseDataSource
{
    public const string IamAuthKey = "Database:IamAuth";

    /// <summary>
    /// The Amazon RDS CA bundle
    /// </summary>
    public static readonly string RdsCaBundlePath =
        Path.Combine(AppContext.BaseDirectory, "Certificates", "rds-global-bundle.pem");

    public static NpgsqlDataSource Create(IConfiguration configuration, string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        if (configuration.GetValue<bool>(IamAuthKey))
        {
            UseIamAuth(builder);
        }

        return builder.Build();
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

        if (settings.SslMode is SslMode.Allow or SslMode.Prefer or SslMode.Require)
        {
            settings.SslMode = SslMode.VerifyFull;
        }

        if (string.IsNullOrEmpty(settings.RootCertificate))
        {
            settings.RootCertificate = RdsCaBundlePath;
        }

        // Called as each connection opens, with the settings of the host that is
        // being opened: the token is signed for one host and port, and with
        // several hosts in the connection string each host's pool passes its own.
        //
        // Generating per connection is cheap: signing is local, and the SDK keeps
        // the credentials from its ambient chain (the ECS task role, an instance
        // profile, or IRSA - the same chain the S3 source uses) until they near
        // expiry, so only a credential refresh reaches the network.
        builder.UsePasswordProvider(
            hostSettings => RDSAuthTokenGenerator.GenerateAuthToken(
                hostSettings.Host!, hostSettings.Port, hostSettings.Username!),
            async (hostSettings, _) => await RDSAuthTokenGenerator.GenerateAuthTokenAsync(
                hostSettings.Host!, hostSettings.Port, hostSettings.Username!));
    }
}
