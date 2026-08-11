using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Data;

public sealed class DmarcAnalyzerDbContext(DbContextOptions<DmarcAnalyzerDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<DmarcReport> DmarcReports => Set<DmarcReport>();
    public DbSet<DmarcReportRecord> DmarcReportRecords => Set<DmarcReportRecord>();
    public DbSet<DmarcReportRecordDkimAuthResult> DmarcReportRecordDkimAuthResults => Set<DmarcReportRecordDkimAuthResult>();
    public DbSet<DmarcReportRecordSpfAuthResult> DmarcReportRecordSpfAuthResults => Set<DmarcReportRecordSpfAuthResult>();
    public DbSet<ReportSource> ReportSources => Set<ReportSource>();
    public DbSet<DmarcReportIngest> DmarcReportIngests => Set<DmarcReportIngest>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<DigestDelivery> DigestDeliveries => Set<DigestDelivery>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MailboxSyncRun> MailboxSyncRuns => Set<MailboxSyncRun>();
    public DbSet<AgencyUser> AgencyUsers => Set<AgencyUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserClientGrant> UserClientGrants => Set<UserClientGrant>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<BackupStreamState> BackupStreamStates => Set<BackupStreamState>();
    public DbSet<MtaStsState> MtaStsStates => Set<MtaStsState>();
    public DbSet<MtaStsPolicy> MtaStsPolicies => Set<MtaStsPolicy>();
    public DbSet<SmtpTlsReport> SmtpTlsReports => Set<SmtpTlsReport>();
    public DbSet<SmtpTlsReportPolicy> SmtpTlsReportPolicies => Set<SmtpTlsReportPolicy>();
    public DbSet<SmtpTlsFailureDetail> SmtpTlsFailureDetails => Set<SmtpTlsFailureDetail>();
    public DbSet<TlsReportIngest> TlsReportIngests => Set<TlsReportIngest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgencyUser>(entity =>
        {
            entity.ToTable("agency_user");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.ToTable("user_session");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CookieId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.HasIndex(x => x.CookieId).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.ExpiresAtUtc);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserIdentity>(entity =>
        {
            entity.ToTable("user_identity");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Issuer).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(255).IsRequired();
            entity.Property(x => x.EmailAtLink).HasMaxLength(255);
            entity.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
            entity.HasIndex(x => x.UserId);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserClientGrant>(entity =>
        {
            entity.ToTable("user_client_grant");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.ClientId }).IsUnique();
            entity.HasIndex(x => x.ClientId);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.CreatedByUser)
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("client");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LegalHold).HasDefaultValue(false);
            entity.Property(x => x.AlertsEnabled).HasDefaultValue(true);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Domain>(entity =>
        {
            entity.ToTable("domain");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.ClientId);
            entity.Property(x => x.DnsPolicy).HasMaxLength(16);
            entity.Property(x => x.DnsLookupStatus).HasMaxLength(16);
            entity.Property(x => x.DnsPolicyInheritedFrom).HasMaxLength(253);
            // The refresh pass picks the least-recently-checked domains first.
            entity.HasIndex(x => x.DnsCheckedAtUtc);

            entity.HasOne(x => x.Client)
                .WithMany(x => x.Domains)
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReportSource>(entity =>
        {
            // Named for what it is rather than for how the first implementation
            // reached it: a row here is a place reports arrive from, and IMAP is
            // one protocol among the ones Protocol can hold. The CLR type is still
            // ReportSource — renaming ~780 identifiers is a separate mechanical
            // change, deliberately not mixed into a migration.
            entity.ToTable("report_source");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Protocol).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PasswordEncrypted).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.LastProcessedUid);
            entity.Property(x => x.LastProcessedUidValidity);
            entity.Property(x => x.DeleteAfterRetention).HasDefaultValue(false);
            entity.HasIndex(x => x.DefaultClientId);

            entity.HasOne(x => x.DefaultClient)
                .WithMany()
                .HasForeignKey(x => x.DefaultClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DmarcReportIngest>(entity =>
        {
            entity.ToTable("dmarc_report_ingest");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PolicyDomain).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReportId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.OrganizationName).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => x.ClientId);
            entity.HasIndex(x => x.ReportSourceId);
            entity.HasIndex(x => new
            {
                x.ClientId,
                x.PolicyDomain,
                x.ReportId,
                x.ReportRangeBeginUtc,
                x.ReportRangeEndUtc,
            }).IsUnique();

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.ReportSource)
                .WithMany()
                .HasForeignKey(x => x.ReportSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MailboxSyncRun>(entity =>
        {
            entity.ToTable("mailbox_sync_run");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(4000);
            entity.HasIndex(x => x.ReportSourceId)
                .HasDatabaseName("IX_mailbox_sync_run_ReportSourceId");
            entity.HasIndex(x => x.StartedAtUtc);

            entity.HasOne(x => x.ReportSource)
                .WithMany()
                .HasForeignKey(x => x.ReportSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DmarcReport>(entity =>
        {
            entity.ToTable("dmarc_report");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReportId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PublishedPolicy).HasMaxLength(16).IsRequired().HasDefaultValue("none");
            entity.Property(x => x.SubdomainPolicy).HasMaxLength(16);
            entity.Property(x => x.PublishedPct).HasDefaultValue(100);
            entity.Property(x => x.DkimAlignment).HasMaxLength(16).IsRequired().HasDefaultValue("relaxed");
            entity.Property(x => x.SpfAlignment).HasMaxLength(16).IsRequired().HasDefaultValue("relaxed");
            entity.HasIndex(x => x.DomainId);
            entity.HasIndex(x => x.ReportSourceId);
            entity.HasIndex(x => new { x.DomainId, x.ReportId, x.RangeBeginUtc, x.RangeEndUtc }).IsUnique();

            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.ReportSource)
                .WithMany()
                .HasForeignKey(x => x.ReportSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DmarcReportRecord>(entity =>
        {
            entity.ToTable("dmarc_report_record");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceIp).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Disposition).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DkimResult).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SpfResult).HasMaxLength(32).IsRequired();
            entity.Property(x => x.HeaderFrom).HasMaxLength(255).IsRequired();
            entity.Property(x => x.EnvelopeFrom).HasMaxLength(255).IsRequired();
            entity.Property(x => x.EnvelopeTo).HasMaxLength(255).IsRequired();
            // Analytics windows filter on this, so it carries the index the join
            // through dmarc_report could never give the planner.
            entity.HasIndex(x => x.ReportRangeBeginUtc);
            entity.HasIndex(x => x.DmarcReportId);

            entity.HasOne(x => x.DmarcReport)
                .WithMany(x => x.Records)
                .HasForeignKey(x => x.DmarcReportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DmarcReportRecordDkimAuthResult>(entity =>
        {
            entity.ToTable("dmarc_report_record_dkim_auth_result");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Selector).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(32).IsRequired();
            entity.Property(x => x.HumanResult).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => x.DmarcReportRecordId);

            entity.HasOne(x => x.DmarcReportRecord)
                .WithMany(x => x.DkimAuthResults)
                .HasForeignKey(x => x.DmarcReportRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationRecipient>(entity =>
        {
            entity.ToTable("notification_recipient");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(16).IsRequired().HasDefaultValue("both");
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.HasIndex(x => x.ClientId);
            // One row per address per scope; a null ClientId is the agency-wide scope.
            entity.HasIndex(x => new { x.ClientId, x.Email }).IsUnique();

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.Property(x => x.ClientName).HasMaxLength(200);
            entity.ToTable("audit_event");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ActorEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetType).HasMaxLength(48);
            entity.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Details).HasMaxLength(4000);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.EventType, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.ClientId, x.OccurredAtUtc });
            // Deliberately no FK to agency_user or client: the trail must outlive
            // the rows it refers to, which is why the actor email is copied in.
        });

        modelBuilder.Entity<DigestDelivery>(entity =>
        {
            entity.ToTable("digest_delivery");
            entity.HasKey(x => x.Id);
            // The idempotency guarantee: one digest per client per period.
            entity.HasIndex(x => new { x.ClientId, x.PeriodStartUtc }).IsUnique();

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertEvent>(entity =>
        {
            entity.ToTable("alert_event");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RuleType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Severity).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Details).HasMaxLength(4000).IsRequired();
            // The cooldown lookup: newest event for a client/domain/rule.
            entity.HasIndex(x => new { x.ClientId, x.RuleType, x.DetectedAtUtc });
            entity.HasIndex(x => x.DetectedAtUtc);

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DmarcReportRecordSpfAuthResult>(entity =>
        {
            entity.ToTable("dmarc_report_record_spf_auth_result");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(32).IsRequired();
            entity.Property(x => x.HumanResult).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => x.DmarcReportRecordId);

            entity.HasOne(x => x.DmarcReportRecord)
                .WithMany(x => x.SpfAuthResults)
                .HasForeignKey(x => x.DmarcReportRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupStreamState>(entity =>
        {
            entity.ToTable("backup_stream_state");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Stream).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(4000);
            // One row per stream; the offload upserts on this.
            entity.HasIndex(x => x.Stream).IsUnique();
        });

        modelBuilder.Entity<MtaStsState>(entity =>
        {
            entity.ToTable("mta_sts_state");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DnsRecordStatus).HasMaxLength(16).IsRequired();
            entity.Property(x => x.RawRecord).HasMaxLength(512);
            entity.Property(x => x.PolicyId).HasMaxLength(64);
            entity.Property(x => x.PreviousPolicyId).HasMaxLength(64);
            entity.Property(x => x.FetchStatus).HasMaxLength(32);
            entity.Property(x => x.FetchDetail).HasMaxLength(1000);
            entity.Property(x => x.Mode).HasMaxLength(16);
            entity.Property(x => x.MxLookupStatus).HasMaxLength(16);
            // One row per domain, current state only.
            entity.HasIndex(x => x.DomainId).IsUnique();
            // The check pass picks the least-recently-checked domains first.
            entity.HasIndex(x => x.LastCheckedAtUtc);

            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MtaStsPolicy>(entity =>
        {
            entity.ToTable("mta_sts_policy");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Enabled).HasDefaultValue(true);
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.MxPatterns).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.PolicyId).HasMaxLength(32).IsRequired();
            // One hosted policy per domain.
            entity.HasIndex(x => x.DomainId).IsUnique();

            // Cascade, unlike dmarc_report's Restrict: the policy is dependent
            // config with no independent value. No domain-delete path exists
            // today, so this is future-proofing rather than live behavior.
            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SmtpTlsReport>(entity =>
        {
            entity.ToTable("smtp_tls_report");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReportId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ContactInfo).HasMaxLength(320);
            entity.HasIndex(x => x.ReportSourceId);
            // The dedupe key. No policy domain — a report spans several — so the
            // organization name disambiguates report-id collisions across reporters.
            entity.HasIndex(x => new
            {
                x.OrganizationName,
                x.ReportId,
                x.RangeBeginUtc,
                x.RangeEndUtc,
            }).IsUnique();
            // The orphan sweep in retention scans on this.
            entity.HasIndex(x => x.RangeEndUtc);

            entity.HasOne(x => x.ReportSource)
                .WithMany()
                .HasForeignKey(x => x.ReportSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SmtpTlsReportPolicy>(entity =>
        {
            entity.ToTable("smtp_tls_report_policy");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PolicyType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PolicyDomain).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PolicyString).HasMaxLength(4000);
            entity.Property(x => x.MxHostPatterns).HasMaxLength(2000);
            entity.HasIndex(x => x.SmtpTlsReportId);
            // Analytics windows filter per domain on the report window begin.
            entity.HasIndex(x => new { x.DomainId, x.ReportRangeBeginUtc });
            // Retention purges on the window end.
            entity.HasIndex(x => x.ReportRangeEndUtc);

            entity.HasOne(x => x.Report)
                .WithMany(x => x.Policies)
                .HasForeignKey(x => x.SmtpTlsReportId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SmtpTlsFailureDetail>(entity =>
        {
            entity.ToTable("smtp_tls_failure_detail");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResultType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FailureCategory).HasMaxLength(16).IsRequired();
            entity.Property(x => x.SendingMtaIp).HasMaxLength(64);
            entity.Property(x => x.ReceivingMxHostname).HasMaxLength(255);
            entity.Property(x => x.ReceivingMxHelo).HasMaxLength(255);
            entity.Property(x => x.ReceivingIp).HasMaxLength(64);
            entity.Property(x => x.AdditionalInformation).HasMaxLength(2000);
            entity.Property(x => x.FailureReasonCode).HasMaxLength(255);
            entity.HasIndex(x => x.SmtpTlsReportPolicyId);

            entity.HasOne(x => x.Policy)
                .WithMany(x => x.FailureDetails)
                .HasForeignKey(x => x.SmtpTlsReportPolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TlsReportIngest>(entity =>
        {
            entity.ToTable("tls_report_ingest");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReportId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PolicyDomains).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.ContactInfo).HasMaxLength(320);
            entity.HasIndex(x => x.ClientId);
            entity.HasIndex(x => x.ReportSourceId);
            // The TLS analogue of the DMARC ledger's five-column key, with the
            // policy domain (meaningless for a multi-domain report) replaced by
            // the organization name.
            entity.HasIndex(x => new
            {
                x.ClientId,
                x.OrganizationName,
                x.ReportId,
                x.ReportRangeBeginUtc,
                x.ReportRangeEndUtc,
            }).IsUnique();
            entity.HasIndex(x => x.ReportRangeEndUtc);

            entity.HasOne(x => x.Client)
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.ReportSource)
                .WithMany()
                .HasForeignKey(x => x.ReportSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
