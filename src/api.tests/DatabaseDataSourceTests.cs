using DmarcAnalyzer.Api.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Building a data source doesn't connect, so these check the settings that are
/// used - not that a token is accepted.
/// </summary>
public sealed class DatabaseDataSourceTests
{
    private static IConfiguration Config(bool? iamAuth = null)
    {
        var values = new Dictionary<string, string?>();
        if (iamAuth is not null)
        {
            values[DatabaseDataSource.IamAuthKey] = iamAuth.Value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void WithoutIamAuth_KeepsThePasswordAndSslMode()
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(),
            "Host=db.example.com;Database=dmarc_analyzer;Username=alice;Password=s3cret;SSL Mode=Disable");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.Disable, settings.SslMode);
        Assert.Equal("alice", settings.Username);
    }

    [Theory]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    [InlineData("Require")]
    public void WithIamAuth_RaisesUnverifiedTlsToVerifyFull(string sslMode)
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(iamAuth: true),
            $"Host=db.example.com;Database=dmarc_analyzer;Username=dmarc;SSL Mode={sslMode}");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, settings.SslMode);
    }

    [Fact]
    public void WithIamAuth_KeepsVerifyCa()
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer;Username=dmarc;SSL Mode=VerifyCA");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(SslMode.VerifyCA, settings.SslMode);
    }

    [Fact]
    public void WithIamAuth_LeavesTheRootCertificateToNpgsql()
    {
        // No CA is chosen here: an RDS endpoint needs the Amazon RDS bundle, an RDS
        // Proxy endpoint needs the system store, and only the operator knows which.
        // Npgsql reads Root Certificate, then PGSSLROOTCERT, then the system store.
        using var dataSource = DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer;Username=dmarc");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.True(string.IsNullOrEmpty(settings.RootCertificate));
        Assert.Equal(SslMode.VerifyFull, settings.SslMode);
    }

    [Fact]
    public void WithIamAuth_KeepsTheOperatorsRootCertificate()
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer;Username=dmarc;Root Certificate=/etc/ssl/own.pem");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal("/etc/ssl/own.pem", settings.RootCertificate);
    }

    [Fact]
    public void WithoutIamAuth_LeavesTheRootCertificateUnset()
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(),
            "Host=db.example.com;Database=dmarc_analyzer;Username=alice;Password=s3cret");

        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.True(string.IsNullOrEmpty(settings.RootCertificate));
        Assert.Equal(SslMode.Prefer, settings.SslMode);
    }

    [Fact]
    public void WithIamAuth_RefusesAPassword()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer;Username=dmarc;Password=s3cret"));

        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public void WithIamAuth_RefusesDisabledTls()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer;Username=dmarc;SSL Mode=Disable"));

        Assert.Contains("TLS", ex.Message);
    }

    [Fact]
    public void WithIamAuth_RefusesAMissingUsername()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseDataSource.Create(
            Config(iamAuth: true),
            "Host=db.example.com;Database=dmarc_analyzer"));
    }

    [Fact]
    public void IamAuthOff_IsTheSameAsUnset()
    {
        using var dataSource = DatabaseDataSource.Create(
            Config(iamAuth: false),
            "Host=db.example.com;Database=dmarc_analyzer;Username=alice;Password=s3cret");

        Assert.Equal("alice", new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Username);
    }

    // The startup line names the CA in the order Npgsql looks for it, so an
    // operator reading the log knows which of the three places won.

    [Fact]
    public void TrustStore_ConnectionStringWinsOverEnvironment()
    {
        var settings = new NpgsqlConnectionStringBuilder("Host=h;Root Certificate=/etc/ssl/rds.pem");

        var description = DatabaseDataSource.DescribeTrustStore(settings, "/ignored.pem", "/also/ignored.crt");

        Assert.StartsWith("/etc/ssl/rds.pem", description);
        Assert.Contains("Root Certificate", description);
    }

    [Fact]
    public void TrustStore_EnvironmentWinsOverLibpqDefault()
    {
        var settings = new NpgsqlConnectionStringBuilder("Host=h");

        var description = DatabaseDataSource.DescribeTrustStore(settings, "/etc/ssl/rds.pem", "/home/x/.postgresql/root.crt");

        Assert.StartsWith("/etc/ssl/rds.pem", description);
        Assert.Contains(DatabaseDataSource.RootCertificateVariable, description);
    }

    [Fact]
    public void TrustStore_LibpqDefaultWhenPresent()
    {
        var settings = new NpgsqlConnectionStringBuilder("Host=h");

        var description = DatabaseDataSource.DescribeTrustStore(settings, null, "/home/x/.postgresql/root.crt");

        Assert.StartsWith("/home/x/.postgresql/root.crt", description);
    }

    [Fact]
    public void TrustStore_SystemStoreWhenNothingIsSet()
    {
        var settings = new NpgsqlConnectionStringBuilder("Host=h");

        var description = DatabaseDataSource.DescribeTrustStore(settings, null, null);

        Assert.Contains("system trust store", description);
    }
}
