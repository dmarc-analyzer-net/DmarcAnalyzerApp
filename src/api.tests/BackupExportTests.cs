using System.Text.Json;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The configuration export is the primary backup, so what it contains — and what it
/// deliberately leaves out — is the contract.
/// <para>
/// Two things are being protected here. First, that a restore is *possible*: the
/// artifact has to carry the mailbox ciphertext and the password hashes, or the operator
/// re-enters every credential. Second, that it is *honest*: it must not carry the IMAP
/// checkpoint or the DNS cache, both of which would make a restored install lie about
/// state it has not re-established, and it must say how many report rows it left behind
/// rather than looking complete.
/// </para>
/// </summary>
public sealed class BackupExportTests
{
    private const string Key = "lSqzPZf0negcljwLKSzvZhIZlvd5hya25OYp1ogntKk=";

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }

    private static BackupExportService Service(DmarcAnalyzerDbContext db, string? key = Key)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:CredentialEncryptionKey"] = key,
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new BackupExportService(db, configuration, NullLogger<BackupExportService>.Instance);
    }

    private static async Task<(Guid ClientId, Guid SourceId)> SeedAsync(
        DmarcAnalyzerDbContext db,
        bool legalHold = false)
    {
        var client = new Client
        {
            Name = "Acme", Slug = "acme", RetentionMonths = 12, LegalHold = legalHold,
            Timezone = "Europe/Copenhagen", AlertComplianceDropPercent = 15,
        };
        var domain = new Domain
        {
            ClientId = client.Id, Name = "acme.example",
            // The DNS cache: written by the worker, must not travel.
            DnsPolicy = "reject", DnsLookupStatus = "found", DnsCheckedAtUtc = DateTime.UtcNow,
        };
        var source = new MailboxSource
        {
            Name = "Acme mailbox", Host = "imap.example", Port = 993, Username = "dmarc@acme.example",
            PasswordEncrypted = "enc:v1:ZmFrZS1jaXBoZXJ0ZXh0", DefaultClientId = client.Id,
            // The IMAP checkpoint: another install's view of the mailbox, must not travel.
            LastProcessedUid = 4711, LastProcessedUidValidity = 9, LastSuccessSyncAtUtc = DateTime.UtcNow,
        };
        var user = new AgencyUser
        {
            Email = "admin@acme.example", DisplayName = "Admin", Role = "agency_admin",
            PasswordHash = "pbkdf2$fake$hash", LastLoginAtUtc = DateTime.UtcNow,
        };

        db.AddRange(client, domain, source, user);
        db.Add(new UserClientGrant { UserId = user.Id, ClientId = client.Id });
        db.Add(new NotificationRecipient { ClientId = client.Id, Email = "ops@acme.example" });
        await db.SaveChangesAsync();

        return (client.Id, source.Id);
    }

    [Fact]
    public async Task CarriesTheCredentialsARestoreCannotRecreate()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var result = await Service(db).ExportAsync(allowPlaintextCredentials: false, default);

        Assert.True(result.IsSuccess);
        var artifact = result.Value!;

        // Without these two the operator re-enters every mailbox password and every
        // account, which is the expensive half of a recovery.
        Assert.Equal("enc:v1:ZmFrZS1jaXBoZXJ0ZXh0", Assert.Single(artifact.MailboxSources).PasswordEncrypted);
        Assert.Equal("pbkdf2$fake$hash", Assert.Single(artifact.Users).PasswordHash);
    }

    [Fact]
    public async Task OmitsTheImapCheckpointAndTheDnsCache()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;
        var json = BackupJson.Serialize(artifact);

        // Asserted against the serialized document, because "the record has no property
        // for it" is the guarantee that matters to a file someone restores from.
        Assert.DoesNotContain("lastProcessedUid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lastProcessedUidValidity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lastSuccessSyncAtUtc", json, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsPolicy", json, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsLookupStatus", json, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsCheckedAtUtc", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lastLoginAtUtc", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatesWhatItLeftBehindRatherThanLookingComplete()
    {
        await using var db = NewDb();
        var (clientId, sourceId) = await SeedAsync(db);
        var domainId = (await db.Domains.SingleAsync()).Id;

        db.Add(new DmarcReport
        {
            DomainId = domainId, ReportSourceId = sourceId, OrganizationName = "google.com",
            ReportId = "r-1", RangeBeginUtc = DateTime.UtcNow.AddDays(-1), RangeEndUtc = DateTime.UtcNow,
            RecordCount = 1,
        });
        await db.SaveChangesAsync();

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;

        Assert.Equal(1, artifact.Manifest.Excluded["dmarc_report"]);
        Assert.Equal(0, artifact.Manifest.Excluded["dmarc_report_record"]);
        Assert.Equal("none", artifact.Manifest.Scope.Reports);
        Assert.True(artifact.Manifest.Scope.Config);

        // No report data travelled, so the client it belongs to is still exported.
        Assert.Equal(clientId, Assert.Single(artifact.Clients).Id);
    }

    [Fact]
    public async Task RefusesWhenCredentialsAreUnprotected()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        // No key configured means the app stored those passwords as plaintext, so the
        // artifact would be a plaintext credential file.
        var result = await Service(db, key: null).ExportAsync(allowPlaintextCredentials: false, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("plaintext", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProceedsUnprotectedOnlyWhenAskedAndSaysSoInTheManifest()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var result = await Service(db, key: null).ExportAsync(allowPlaintextCredentials: true, default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Manifest.CredentialsProtected);

        // Nothing to fingerprint, and claiming one would imply protection that is absent.
        Assert.Null(result.Value!.Manifest.EncryptionKeyFingerprint);
    }

    [Fact]
    public async Task FingerprintsTheKeyWithoutCarryingIt()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;
        var json = BackupJson.Serialize(artifact);

        Assert.Equal(CredentialKeyFingerprint.Compute(Key), artifact.Manifest.EncryptionKeyFingerprint);
        Assert.StartsWith("sha256:", artifact.Manifest.EncryptionKeyFingerprint!, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagsLegalHoldClientsBecauseReIngestionIsNoAnswerForThem()
    {
        await using var db = NewDb();
        await SeedAsync(db, legalHold: true);

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;

        Assert.Equal("acme", Assert.Single(artifact.Manifest.LegalHoldClients));
    }

    [Fact]
    public async Task ReportsUnknownMigrationStateRatherThanFailingOnANonRelationalContext()
    {
        // The in-memory provider has no migration history. An export that threw here
        // would be untestable; one that invented a migration id would be worse.
        await using var db = NewDb();
        await SeedAsync(db);

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;

        Assert.Null(artifact.Manifest.MigrationId);
        Assert.Equal(0, artifact.Manifest.MigrationCount);
    }

    /// <summary>
    /// The artifact is a published format: a file written today gets read by a build from
    /// another year. Nothing in the app pins JSON property names — responses rely on
    /// ASP.NET's default policy — so this asserts the names directly. A failure here means
    /// a rename just invalidated every stored artifact, and the fix is to restore the
    /// name, not to update the test.
    /// </summary>
    [Fact]
    public async Task PropertyNamesArePinned()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var artifact = (await Service(db).ExportAsync(false, default)).Value!;
        using var document = JsonDocument.Parse(BackupJson.Serialize(artifact));
        var root = document.RootElement;

        Assert.Equal(
            ["manifest", "clients", "domains", "mailboxSources", "notificationRecipients",
             "users", "userIdentities", "grants", "mtaStsPolicies"],
            root.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(
            ["formatVersion", "exportedAtUtc", "appVersion", "migrationId", "migrationCount",
             "encryptionKeyFingerprint", "credentialsProtected", "scope", "excluded",
             "legalHoldClients"],
            root.GetProperty("manifest").EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(
            ["id", "name", "slug", "isActive", "retentionMonths", "legalHold", "alertsEnabled",
             "alertComplianceDropPercent", "alertMinMessages", "timezone", "createdAtUtc",
             "updatedAtUtc"],
            root.GetProperty("clients")[0].EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(
            ["id", "name", "protocol", "host", "port", "useTls", "username", "passwordEncrypted",
             "defaultClientId", "isActive", "createdAtUtc", "updatedAtUtc"],
            root.GetProperty("mailboxSources")[0].EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(1, root.GetProperty("manifest").GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void FingerprintIdentifiesAKeyAndRefusesToGuess()
    {
        var other = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

        Assert.NotEqual(CredentialKeyFingerprint.Compute(Key), CredentialKeyFingerprint.Compute(other));

        // Whitespace around a configured value is not a different key.
        Assert.Equal(CredentialKeyFingerprint.Compute(Key), CredentialKeyFingerprint.Compute($"  {Key}\n"));

        Assert.True(CredentialKeyFingerprint.Matches(CredentialKeyFingerprint.Compute(Key), Key));
        Assert.False(CredentialKeyFingerprint.Matches(CredentialKeyFingerprint.Compute(Key), other));

        // "Unknown" on either side is never a match: importing sources that can never be
        // decrypted is the failure this guard exists for.
        Assert.False(CredentialKeyFingerprint.Matches(null, Key));
        Assert.False(CredentialKeyFingerprint.Matches(CredentialKeyFingerprint.Compute(Key), null));
    }
}
