using DmarcAnalyzer.Api.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ConnectionStringResolverTests
{
    [Fact]
    public void FromDatabaseUrl_ParsesHostPortDatabaseCredentials()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringResolver.FromDatabaseUrl("postgres://alice:s3cret@db.example.com:6543/dmarc_analyzer"));

        Assert.Equal("db.example.com", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("dmarc_analyzer", builder.Database);
        Assert.Equal("alice", builder.Username);
        Assert.Equal("s3cret", builder.Password);
    }

    [Fact]
    public void FromDatabaseUrl_DefaultsPortWhenOmitted()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringResolver.FromDatabaseUrl("postgres://alice:s3cret@db.example.com/dmarc_analyzer"));

        Assert.Equal(5432, builder.Port);
    }

    [Fact]
    public void FromDatabaseUrl_UnescapesCredentials()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringResolver.FromDatabaseUrl("postgres://ali%40ce:p%40ss%3Aword@db.example.com/dmarc_analyzer"));

        Assert.Equal("ali@ce", builder.Username);
        Assert.Equal("p@ss:word", builder.Password);
    }

    [Fact]
    public void FromDatabaseUrl_AcceptsPostgresqlScheme()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringResolver.FromDatabaseUrl("postgresql://alice:s3cret@db.example.com/dmarc_analyzer"));

        Assert.Equal("db.example.com", builder.Host);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void FromDatabaseUrl_MapsSslMode(string queryValue, SslMode expected)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringResolver.FromDatabaseUrl($"postgres://alice:s3cret@db.example.com/dmarc_analyzer?sslmode={queryValue}"));

        Assert.Equal(expected, builder.SslMode);
    }

    [Fact]
    public void FromDatabaseUrl_RejectsUnrecognizedSslMode()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConnectionStringResolver.FromDatabaseUrl("postgres://alice:s3cret@db.example.com/dmarc_analyzer?sslmode=bogus"));
    }

    [Fact]
    public void FromDatabaseUrl_RejectsNonPostgresScheme()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConnectionStringResolver.FromDatabaseUrl("mysql://alice:s3cret@db.example.com/dmarc_analyzer"));
    }

    [Fact]
    public void Resolve_PrefersDatabaseUrlOverConnectionStringsDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "postgres://alice:s3cret@db.example.com/dmarc_analyzer",
                ["ConnectionStrings:Default"] = "Host=other;Port=5432;Database=other;Username=other;Password=other",
            })
            .Build();

        var builder = new NpgsqlConnectionStringBuilder(ConnectionStringResolver.Resolve(configuration));

        Assert.Equal("db.example.com", builder.Host);
    }

    [Fact]
    public void Resolve_FallsBackToConnectionStringsDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=other;Port=5432;Database=other;Username=other;Password=other",
            })
            .Build();

        var builder = new NpgsqlConnectionStringBuilder(ConnectionStringResolver.Resolve(configuration));

        Assert.Equal("other", builder.Host);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNeitherIsSet()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Null(ConnectionStringResolver.Resolve(configuration));
    }
}
