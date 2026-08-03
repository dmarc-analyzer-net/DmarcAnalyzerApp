using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DmarcAnalyzer.Api.Data;

/// <summary>
/// Resolves the Npgsql connection string, preferring a <c>postgres://</c> URI in
/// <c>DATABASE_URL</c> — the convention used by Render, Heroku, Railway, and most
/// managed-Postgres platforms — over the ADO.NET-format <c>ConnectionStrings__Default</c>.
/// Without this, wiring a platform-provisioned database means hand-assembling the
/// ADO.NET string from separate host/port/user/password fields, or pasting it in
/// by hand during a Blueprint-style deploy.
/// </summary>
public static class ConnectionStringResolver
{
    /// <summary>Looks up DATABASE_URL first, then ConnectionStrings:Default. Null if neither is set.</summary>
    public static string? Resolve(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"];
        return !string.IsNullOrEmpty(databaseUrl)
            ? FromDatabaseUrl(databaseUrl)
            : configuration.GetConnectionString("Default");
    }

    /// <summary>Converts a postgres:// or postgresql:// URI into Npgsql's keyword=value format.</summary>
    public static string FromDatabaseUrl(string databaseUrl)
    {
        Uri uri;
        try
        {
            uri = new Uri(databaseUrl);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException($"DATABASE_URL is not a valid URI: {ex.Message}", ex);
        }

        if (uri.Scheme != "postgres" && uri.Scheme != "postgresql")
        {
            throw new InvalidOperationException(
                $"DATABASE_URL must use the postgres:// or postgresql:// scheme, got '{uri.Scheme}://'.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port == -1 ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
        };

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length > 0 && userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        foreach (var (key, value) in ParseQuery(uri.Query))
        {
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = ParseSslMode(value);
            }
        }

        return builder.ConnectionString;
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => throw new InvalidOperationException($"DATABASE_URL has an unrecognized sslmode '{value}'."),
    };

    private static IEnumerable<(string Key, string Value)> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            yield break;
        }

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                yield return (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
            }
        }
    }
}
