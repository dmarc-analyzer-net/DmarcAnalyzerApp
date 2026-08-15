using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Which protocols a source may be created as, and what each one is then allowed to carry.
/// <para>
/// <c>pop3</c> is the interesting case and it has been in both states. It validated here for a
/// long time while nothing acted on it, so a POP3 source could be created and would silently
/// never ingest a report; it was removed on that basis and is back now that
/// <c>Pop3MailboxTransport</c> reads it. These tests are what tie the two together — the value
/// is accepted here <em>and</em> the source is polled — so the two halves cannot drift apart
/// again without something going red.
/// </para>
/// </summary>
public sealed class ReportSourceProtocolTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static async Task<Guid> SeedClientAsync(DmarcAnalyzerDbContext db)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 12, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static ReportSourceService NewService(DmarcAnalyzerDbContext db)
        => new(db, new NullCredentialProtector());

    private static CreateReportSourceRequest Mailbox(string protocol, Guid clientId, int port) => new()
    {
        Name = $"{protocol} mailbox",
        Protocol = protocol,
        Host = $"{protocol}.example.test",
        Port = port,
        UseTls = true,
        Username = "rua@acme.test",
        Password = "secret",
        DefaultClientId = clientId,
    };

    [Fact]
    public async Task APop3SourceCanBeCreated()
    {
        using var db = NewDb();
        var clientId = await SeedClientAsync(db);

        var result = await NewService(db).CreateAsync(
            Mailbox(ReportSourceProtocols.Pop3, clientId, 995), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportSourceProtocols.Pop3, result.Value!.Protocol);
        Assert.Equal(995, result.Value.Port);
    }

    /// <summary>
    /// The half that was missing last time. Accepting the value is only half of supporting a
    /// protocol; the worker has to pick the row up, and it selects on this predicate.
    /// </summary>
    [Fact]
    public async Task APop3SourceIsPolled()
    {
        using var db = NewDb();
        var clientId = await SeedClientAsync(db);

        await NewService(db).CreateAsync(
            Mailbox(ReportSourceProtocols.Pop3, clientId, 995), CancellationToken.None);

        var polled = await db.ReportSources
            .Where(x => x.IsActive && ReportSourceProtocols.Polled.Contains(x.Protocol))
            .ToListAsync();

        Assert.Single(polled);
    }

    /// <summary>
    /// A POP3 mailbox needs the same transport settings an IMAP one does, so the same
    /// requirement applies: it is only the pushed source that may arrive without them.
    /// </summary>
    [Fact]
    public async Task APop3SourceStillNeedsAHostAndCredentials()
    {
        using var db = NewDb();
        var clientId = await SeedClientAsync(db);

        var request = Mailbox(ReportSourceProtocols.Pop3, clientId, 995);
        request.Host = string.Empty;

        var result = await NewService(db).CreateAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task AnUnknownProtocolIsStillRefused()
    {
        using var db = NewDb();
        var clientId = await SeedClientAsync(db);

        var result = await NewService(db).CreateAsync(
            Mailbox("jmap", clientId, 443), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("protocol must be imap, pop3, s3 or api", result.Error);
    }

    /// <summary>
    /// A row created before POP3 worked is an ordinary POP3 source now, not a legacy shape to
    /// be tolerated — so moving it to IMAP, or back, is an ordinary edit.
    /// </summary>
    [Fact]
    public async Task AnExistingPop3SourceCanBeMovedToImap()
    {
        using var db = NewDb();
        var clientId = await SeedClientAsync(db);

        var created = await NewService(db).CreateAsync(
            Mailbox(ReportSourceProtocols.Pop3, clientId, 995), CancellationToken.None);

        var updated = await NewService(db).UpdateAsync(
            created.Value!.Id,
            new UpdateReportSourceRequest { Protocol = ReportSourceProtocols.Imap, Port = 993 },
            CancellationToken.None);

        Assert.True(updated.IsSuccess);
        Assert.Equal(ReportSourceProtocols.Imap, updated.Value!.Protocol);
    }
}
