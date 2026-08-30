using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The header-from / envelope-from lists on the per-source panel, against a real database.
/// <para>
/// Here rather than in src/api.tests because the filter that drops the identifiers a
/// reporter never sent has to reach the database to be worth anything — it exists to keep
/// an omitted value from consuming one of ten slots, which only means something if the
/// cap is applied by the database. It trims, and InMemory would evaluate that in C#
/// whether or not Npgsql can translate it, so the fast suite cannot tell a working query
/// from one that throws at runtime and 500s the panel.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SourceIdentifierAnalyticsPostgresTests(PostgresFixture fixture)
{
    private const string SourceIp = "203.0.113.5";

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

    /// <summary>No DMARC record published anywhere; the panel's DNS lookups are not the subject here.</summary>
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

    private static async Task<Guid> SeedAsync(
        DmarcAnalyzerDbContext db, params (string EnvelopeFrom, int Count)[] records)
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
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domain.Id, ReportSourceId = source.Id,
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = DateTime.UtcNow.AddDays(-1),
            RangeEndUtc = DateTime.UtcNow.AddDays(-1).AddHours(23),
            RecordCount = records.Length, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "reject", SubdomainPolicy = "reject", PublishedPct = 100,
        };
        db.AddRange(client, domain, source, report);
        foreach (var r in records)
        {
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id,
                ReportRangeBeginUtc = report.RangeBeginUtc,
                SourceIp = SourceIp, MessageCount = r.Count,
                Disposition = "none", DkimResult = "pass", SpfResult = "pass",
                HeaderFrom = "acme.example", EnvelopeFrom = r.EnvelopeFrom,
            });
        }

        await db.SaveChangesAsync();
        return domain.Id;
    }

    /// <summary>
    /// An omitted envelope-from is typically the largest group a source has. Dropped after
    /// the top-ten cap it took first place and was then discarded, so the panel showed nine
    /// values and gave no sign a tenth had been displaced (#196).
    /// </summary>
    [Fact]
    public async Task AnOmittedIdentifierDoesNotDisplaceARealValueFromTheTopTen()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        var records = Enumerable.Range(1, 10)
            .Select(i => ($"sender{i:00}.example", i))
            .Append((string.Empty, 1_000))
            .ToArray();
        var domainId = await SeedAsync(db, records);

        var detail = await Analytics(db).GetSourceDetailAsync(domainId, SourceIp, 30, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(10, detail.EnvelopeFroms.Count);
        Assert.DoesNotContain(detail.EnvelopeFroms, x => x.Value == string.Empty);
        Assert.Contains(detail.EnvelopeFroms, x => x.Value == "sender01.example");
    }

    /// <summary>
    /// Nothing trims these on the way in, so a reporter that pretty-prints its XML stores the
    /// indentation around the element as the value. Same absence, same rule — and this is the
    /// assertion that proves <c>Trim()</c> reaches PostgreSQL as <c>btrim</c> rather than
    /// throwing.
    /// </summary>
    [Fact]
    public async Task AWhitespaceOnlyIdentifierIsTreatedAsOmitted()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        var domainId = await SeedAsync(db, ("\n      \n    ", 500), ("sender.example", 1));

        var detail = await Analytics(db).GetSourceDetailAsync(domainId, SourceIp, 30, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(["sender.example"], detail.EnvelopeFroms.Select(x => x.Value));
    }

    /// <summary>
    /// The null reverse-path is a value, not an absence: the source really did send mail with
    /// an empty envelope sender, which is what a bounce looks like. It survives the filter,
    /// and the panel labels it — see ValueList in DomainDetailPage.tsx.
    /// </summary>
    [Fact]
    public async Task TheNullReversePathIsKeptAsAReportedValue()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        var domainId = await SeedAsync(db, ("<>", 42));

        var detail = await Analytics(db).GetSourceDetailAsync(domainId, SourceIp, 30, CancellationToken.None);

        Assert.NotNull(detail);
        var nullSender = Assert.Single(detail.EnvelopeFroms);
        Assert.Equal("<>", nullSender.Value);
        Assert.Equal(42, nullSender.Messages);
    }
}
