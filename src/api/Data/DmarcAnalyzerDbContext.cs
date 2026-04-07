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
    public DbSet<MailboxSource> MailboxSources => Set<MailboxSource>();
    public DbSet<DmarcReportIngest> DmarcReportIngests => Set<DmarcReportIngest>();
    public DbSet<MailboxSyncRun> MailboxSyncRuns => Set<MailboxSyncRun>();
    public DbSet<AgencyUser> AgencyUsers => Set<AgencyUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

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

        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("client");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Domain>(entity =>
        {
            entity.ToTable("domain");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.ClientId);

            entity.HasOne(x => x.Client)
                .WithMany(x => x.Domains)
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MailboxSource>(entity =>
        {
            entity.ToTable("mailbox_source");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Protocol).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PasswordEncrypted).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.LastProcessedUid);
            entity.Property(x => x.LastProcessedUidValidity);
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
            entity.HasIndex(x => x.MailboxSourceId);
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

            entity.HasOne(x => x.MailboxSource)
                .WithMany()
                .HasForeignKey(x => x.MailboxSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MailboxSyncRun>(entity =>
        {
            entity.ToTable("mailbox_sync_run");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(4000);
            entity.HasIndex(x => x.MailboxSourceId)
                .HasDatabaseName("IX_mailbox_sync_run_MailboxSourceId");
            entity.HasIndex(x => x.StartedAtUtc);

            entity.HasOne(x => x.MailboxSource)
                .WithMany()
                .HasForeignKey(x => x.MailboxSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DmarcReport>(entity =>
        {
            entity.ToTable("dmarc_report");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReportId).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => x.DomainId);
            entity.HasIndex(x => x.MailboxSourceId);
            entity.HasIndex(x => new { x.DomainId, x.ReportId, x.RangeBeginUtc, x.RangeEndUtc }).IsUnique();

            entity.HasOne(x => x.Domain)
                .WithMany()
                .HasForeignKey(x => x.DomainId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.MailboxSource)
                .WithMany()
                .HasForeignKey(x => x.MailboxSourceId)
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
    }
}
