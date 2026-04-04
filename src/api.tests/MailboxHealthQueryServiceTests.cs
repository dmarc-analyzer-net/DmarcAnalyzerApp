using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class MailboxHealthQueryServiceTests
{
    [Fact]
    public async Task ListAsync_ReturnsLatestRunPerMailbox()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var db = new DmarcAnalyzerDbContext(options);

        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Client",
            Slug = "client",
            Timezone = "UTC",
            RetentionMonths = 12,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        var mailbox = new MailboxSource
        {
            Id = Guid.NewGuid(),
            Name = "Inbox A",
            DefaultClientId = client.Id,
            Protocol = "imap",
            Host = "imap.example.com",
            Port = 993,
            UseTls = true,
            Username = "dmarc@example.com",
            PasswordEncrypted = "cipher",
            IsActive = true,
            LastSuccessSyncAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastProcessedUid = 120,
            LastProcessedUidValidity = 999,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        db.Clients.Add(client);
        db.MailboxSources.Add(mailbox);

        db.MailboxSyncRuns.AddRange(
            new MailboxSyncRun
            {
                Id = Guid.NewGuid(),
                MailboxSourceId = mailbox.Id,
                Trigger = "scheduled",
                Status = "failed",
                StartedAtUtc = DateTime.UtcNow.AddHours(-2),
                FinishedAtUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(1),
                MessagesScanned = 10,
                AttachmentsProcessed = 9,
                ReportsInserted = 3,
                ReportsSkippedAsDuplicate = 6,
                ParseFailures = 0,
                Error = "network",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            },
            new MailboxSyncRun
            {
                Id = Guid.NewGuid(),
                MailboxSourceId = mailbox.Id,
                Trigger = "scheduled",
                Status = "success",
                StartedAtUtc = DateTime.UtcNow.AddHours(-1),
                FinishedAtUtc = DateTime.UtcNow.AddHours(-1).AddMinutes(2),
                MessagesScanned = 25,
                AttachmentsProcessed = 25,
                ReportsInserted = 20,
                ReportsSkippedAsDuplicate = 5,
                ParseFailures = 0,
                Error = null,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });

        await db.SaveChangesAsync();

        var service = new MailboxHealthQueryService(db);
        var result = await service.ListAsync(mailbox.Id, CancellationToken.None);

        Assert.Single(result);
        var item = result[0];
        Assert.Equal(mailbox.Id, item.MailboxSourceId);
        Assert.Equal("success", item.LastRunStatus);
        Assert.Equal(25, item.LastRunMessagesScanned);
        Assert.Equal(20, item.LastRunReportsInserted);
        Assert.Equal(5, item.LastRunReportsSkippedAsDuplicate);
        Assert.Equal(0, item.LastRunParseFailures);
        Assert.Equal(120, item.LastProcessedUid);
    }
}
