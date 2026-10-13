using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The report-notes query against a real database.
/// <para>
/// Here because its info-only filter compares the jsonb column with <c>'[]'</c>. InMemory
/// compares two strings and passes whether or not Npgsql can translate the comparison or
/// Postgres accepts a jsonb-to-parameter one, so the fast suite cannot tell a working
/// query from one that throws at runtime and 500s the panel.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ReportNotesPostgresTests(PostgresFixture fixture)
{
    private sealed class AdminUser : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "test@agency.tld";
        public string Role => Roles.AgencyAdmin;
        public IReadOnlyCollection<Guid> AllowedClientIds => [];
        public bool IsAdmin => true;
        public bool IsAgencyStaff => true;
        public bool CanAccessClient(Guid clientId) => true;
    }

    private sealed class NoDns : IDnsTxtResolver
    {
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
            => Task.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());
    }

    private static AnalyticsQueryService Analytics(DmarcAnalyzerDbContext db)
    {
        var policyResolver = new DmarcPolicyResolver(new NoDns());
        return new AnalyticsQueryService(
            db,
            new AdminUser(),
            policyResolver,
            new DnsPolicyCache(db, policyResolver, NullLogger<DnsPolicyCache>.Instance),
            NullLogger<AnalyticsQueryService>.Instance);
    }

    private static readonly DmarcReportParseMessage Namespace =
        DmarcReportParseMessage.Info("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility");

    private static readonly DmarcReportParseMessage Repair =
        DmarcReportParseMessage.Warning("replaced unrecognised policy_evaluated/dkim value 'x' with 'fail' for compatibility");

    [Fact]
    public async Task InfoOnlyFilter_TranslatesAndTellsAnEmptyListFromANullOne()
    {
        await fixture.ResetAsync();
        Guid domainId;
        await using (var db = fixture.CreateContext())
        {
            var client = new Client
            {
                Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
                RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            };
            var domain = new Domain
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true,
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            };
            var source = new ReportSource
            {
                Id = Guid.NewGuid(), Name = "mailbox", Protocol = "imap",
                Host = "imap.example.test", Port = 993, UseTls = true,
                Username = "u", PasswordEncrypted = "x", DefaultClientId = client.Id, IsActive = true,
            };
            db.AddRange(client, domain, source);
            domainId = domain.Id;

            var end = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            DmarcReport Report(string reporter, int daysAgo, IReadOnlyList<DmarcReportParseMessage>? messages) => new()
            {
                Id = Guid.NewGuid(), DomainId = domain.Id, ReportSourceId = source.Id,
                OrganizationName = reporter, ReportId = reporter, RecordCount = 1,
                RangeBeginUtc = end.AddDays(-daysAgo - 1), RangeEndUtc = end.AddDays(-daysAgo),
                IngestedAtUtc = end.AddDays(-daysAgo),
                ParseMessages = messages is null ? null : DmarcReportParseMessagesJson.Serialize(messages),
                ParseWarningCount = messages?.Count(x => DmarcReportParseSeverity.IsAtLeastWarning(x.Severity)),
                ParseMessagesOrigin = messages is null ? null : DmarcReportParseOrigins.Ingest,
            };

            db.AddRange(
                Report("repaired.example", 1, [Namespace, Repair]),
                Report("info-only.example", 0, [Namespace]),
                Report("clean.example", 2, []),
                Report("old.example", 3, null));
            await db.SaveChangesAsync();
        }

        await using var query = fixture.CreateContext();
        var service = Analytics(query);

        var warningsOnly = await service.GetReportNotesAsync(domainId, 30, false, CancellationToken.None);
        Assert.NotNull(warningsOnly);
        Assert.Equal(4, warningsOnly.ReportsInWindow);
        Assert.Equal(1, warningsOnly.ReportsWithWarnings);
        Assert.Equal(1, warningsOnly.ReportsWithInfoOnly);
        Assert.Equal(1, warningsOnly.ReportsNotRecorded);
        Assert.Equal(["repaired.example"], warningsOnly.Reports.Select(x => x.OrganizationName));
        Assert.Equal([Namespace, Repair], warningsOnly.Reports[0].Messages);

        var withInfo = await service.GetReportNotesAsync(domainId, 30, true, CancellationToken.None);
        Assert.NotNull(withInfo);
        Assert.Equal(["info-only.example", "repaired.example"], withInfo.Reports.Select(x => x.OrganizationName));
    }
}
