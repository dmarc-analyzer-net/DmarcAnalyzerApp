using System.Text;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The offload pass, and specifically the two ways it could destroy the thing it exists to
/// protect.
/// <para>
/// <c>config/latest.json</c> is the one key this feature overwrites, so a pass that
/// succeeds while producing a truncated or empty document would replace a good artifact
/// with a useless one — and nobody would find out until a recovery. Hence
/// validate-stage-verify-promote, asserted here.
/// </para>
/// <para>
/// The other is credentials: with no encryption key the app stores mailbox passwords in
/// plaintext, and shipping those to a bucket is meaningfully worse than leaving them in
/// Postgres, because the blast radius stops being the database.
/// </para>
/// </summary>
public sealed class BackupOffloadTests
{
    private const string Key = "lSqzPZf0negcljwLKSzvZhIZlvd5hya25OYp1ogntKk=";

    /// <summary>
    /// An in-process bucket. Records every put and copy so the promotion order can be
    /// asserted, and can be told to truncate a put — which is the failure the staging step
    /// exists to catch and cannot otherwise be reproduced.
    /// </summary>
    private sealed class FakeStorage : IObjectStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public List<string> Puts { get; } = [];
        public List<(string From, string To)> Copies { get; } = [];
        public bool TruncatePuts { get; set; }
        public bool ThrowOnPut { get; set; }
        public ObjectStorageVersioning Versioning { get; set; } = ObjectStorageVersioning.Enabled;

        public bool IsConfigured { get; set; } = true;

        public string Describe() => "fake://bucket";

        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
        {
            if (ThrowOnPut)
            {
                throw new InvalidOperationException("bucket unreachable");
            }

            Puts.Add(key);
            Objects[key] = TruncatePuts ? content[..(content.Length / 2)] : content;

            return Task.CompletedTask;
        }

        public Task<long?> GetLengthAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(key, out var value) ? value.Length : (long?)null);

        public Task<byte[]?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(key, out var value) ? value : null);

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct)
        {
            Copies.Add((sourceKey, destinationKey));
            Objects[destinationKey] = Objects[sourceKey];

            return Task.CompletedTask;
        }

        public Task<ObjectStorageVersioning> GetVersioningAsync(CancellationToken ct)
            => Task.FromResult(Versioning);
    }

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }

    private static BackupOffloadService Service(
        DmarcAnalyzerDbContext db,
        FakeStorage storage,
        string? key = Key,
        BackupOptions? options = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CredentialEncryptionKey"] = key,
            })
            .Build();

        var export = new BackupExportService(db, configuration, NullLogger<BackupExportService>.Instance);

        return new BackupOffloadService(
            db, export, storage, configuration,
            Options.Create(options ?? new BackupOptions { Bucket = "bucket" }),
            NullLogger<BackupOffloadService>.Instance);
    }

    private static async Task SeedAsync(DmarcAnalyzerDbContext db)
    {
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Add(client);
        db.Add(new Domain { ClientId = client.Id, Name = "acme.example" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PromotesLatestOnlyAfterStagingItAndVerifyingTheLength()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage();

        var result = await Service(db, storage).RunAsync(default);

        Assert.True(result.Ran);
        Assert.Null(result.Error);

        // Staged first, then promoted — never written straight over latest.json.
        Assert.Equal("dmarc/config/.staging/latest.json", Assert.Single(storage.Puts));
        Assert.Contains(("dmarc/config/.staging/latest.json", "dmarc/config/latest.json"), storage.Copies);

        var promoted = Encoding.UTF8.GetString(storage.Objects["dmarc/config/latest.json"]);
        using var document = JsonDocument.Parse(promoted);
        Assert.Equal(1, document.RootElement.GetProperty("clients").GetArrayLength());
    }

    [Fact]
    public async Task LeavesLatestUntouchedWhenTheUploadArrivesTruncated()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        // Establish a known-good latest.json first, so there is something to destroy.
        var storage = new FakeStorage();
        await Service(db, storage).RunAsync(default);
        var good = storage.Objects["dmarc/config/latest.json"];

        storage.TruncatePuts = true;
        storage.Copies.Clear();

        var result = await Service(db, storage).RunAsync(default);

        Assert.NotNull(result.Error);
        Assert.Contains("expected", result.Error!, StringComparison.OrdinalIgnoreCase);

        // The whole point: a half-written upload does not become the only copy.
        Assert.Equal(good, storage.Objects["dmarc/config/latest.json"]);
        Assert.DoesNotContain(storage.Copies, c => c.To == "dmarc/config/latest.json");
    }

    [Fact]
    public async Task WritesTheDatedCopyBeforePromoting()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage();

        await Service(db, storage).RunAsync(default);

        // Ordered this way so a dated copy survives even when promotion is what fails.
        var dated = storage.Copies.FindIndex(c => c.To.StartsWith("dmarc/config/2", StringComparison.Ordinal));
        var latest = storage.Copies.FindIndex(c => c.To == "dmarc/config/latest.json");

        Assert.True(dated >= 0, "expected a dated snapshot copy");
        Assert.True(dated < latest, "the dated copy must be written before latest.json is promoted");
    }

    [Fact]
    public async Task RefusesToShipPlaintextCredentials()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage();

        var result = await Service(db, storage, key: null).RunAsync(default);

        Assert.False(result.Ran);
        Assert.Contains("CredentialEncryptionKey", result.Error!, StringComparison.Ordinal);
        Assert.Empty(storage.Puts);

        // Recorded, so the console can show why backups are not happening rather than
        // leaving the operator to infer it from an absence.
        var state = await db.BackupStreamStates.SingleAsync(x => x.Stream == BackupOffloadService.ConfigStream);
        Assert.NotNull(state.LastError);
        Assert.Null(state.LastSuccessAtUtc);
    }

    [Fact]
    public async Task IsInertWithoutABucket()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage { IsConfigured = false };

        var result = await Service(db, storage).RunAsync(default);

        Assert.False(result.Ran);
        Assert.Null(result.Error);
        Assert.Empty(storage.Puts);

        // No bucket is the default, not a fault, so nothing is recorded as failing.
        Assert.Empty(await db.BackupStreamStates.ToListAsync());
    }

    [Fact]
    public async Task RecordsFailureWithoutLosingThePreviousSuccess()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage();

        await Service(db, storage).RunAsync(default);
        var succeededAt = (await db.BackupStreamStates
            .SingleAsync(x => x.Stream == BackupOffloadService.ConfigStream)).LastSuccessAtUtc;
        Assert.NotNull(succeededAt);

        storage.ThrowOnPut = true;
        db.ChangeTracker.Clear();
        await Service(db, storage).RunAsync(default);

        var state = await db.BackupStreamStates
            .SingleAsync(x => x.Stream == BackupOffloadService.ConfigStream);

        // "Last succeeded at X, currently failing" is the state an operator needs to see.
        Assert.Equal(succeededAt, state.LastSuccessAtUtc);
        Assert.Contains("unreachable", state.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShipsHistoryAndAdvancesTheWatermark()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        db.Add(new AuditEvent
        {
            ActorType = "user", ActorEmail = "admin@acme.example", EventType = "client.created",
            Summary = "Created client Acme", OccurredAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        var result = await Service(db, storage).RunAsync(default);

        Assert.Equal(1, result.HistoryObjects["audit_event"]);
        Assert.Contains(storage.Puts, k => k.StartsWith("dmarc/history/audit_event/2026/", StringComparison.Ordinal));

        var state = await db.BackupStreamStates.SingleAsync(x => x.Stream == "audit_event");
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), state.WatermarkUtc);

        // One JSON object per line, so a reader can take a row at a time.
        var body = Encoding.UTF8.GetString(
            storage.Objects[storage.Puts.First(k => k.Contains("audit_event", StringComparison.Ordinal))]);
        Assert.Single(body.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task ReShipsAnOverlapSoARowCommittedMidPassIsNeverLost()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var at = DateTime.UtcNow.AddMinutes(-1);
        db.Add(new AuditEvent
        {
            ActorType = "user", ActorEmail = "a@b.c", EventType = "client.created",
            Summary = "one", OccurredAtUtc = at,
        });
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        await Service(db, storage).RunAsync(default);

        // Second pass with nothing new: the overlap window means the same row is shipped
        // again rather than the watermark quietly stepping past anything near it.
        db.ChangeTracker.Clear();
        storage.Puts.Clear();
        var second = await Service(db, storage).RunAsync(default);

        Assert.Equal(1, second.HistoryObjects["audit_event"]);
    }

    [Fact]
    public async Task HistoryCanBeTurnedOffWithoutAffectingTheSnapshot()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        db.Add(new AuditEvent
        {
            ActorType = "user", ActorEmail = "a@b.c", EventType = "client.created", Summary = "one",
        });
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        var options = new BackupOptions { Bucket = "bucket", IncludeHistory = false };

        var result = await Service(db, storage, options: options).RunAsync(default);

        Assert.True(result.Ran);
        Assert.Empty(result.HistoryObjects);
        Assert.DoesNotContain(storage.Puts, k => k.Contains("/history/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusSurfacesTheThingsThatFailQuietly()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var storage = new FakeStorage { Versioning = ObjectStorageVersioning.Disabled };

        await Service(db, storage).RunAsync(default);
        db.ChangeTracker.Clear();
        var status = await Service(db, storage).GetStatusAsync(default);

        Assert.True(status.OffloadConfigured);
        Assert.True(status.CredentialsProtected);
        // Disabled versioning under an overwritten latest.json is the quiet risk.
        Assert.Equal("disabled", status.BucketVersioning);
        Assert.NotNull(status.LastSuccessfulOffloadAtUtc);
        Assert.Contains(status.Streams, s => s.Stream == "audit_event");
    }

    [Fact]
    public async Task StatusReportsUnprotectedCredentialsBecauseOffloadWillNotRun()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var status = await Service(db, new FakeStorage(), key: null).GetStatusAsync(default);

        Assert.False(status.CredentialsProtected);
    }
}
