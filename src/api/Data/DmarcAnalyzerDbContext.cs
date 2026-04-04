using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Data;

public sealed class DmarcAnalyzerDbContext(DbContextOptions<DmarcAnalyzerDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<MailboxSource> MailboxSources => Set<MailboxSource>();
    public DbSet<DmarcReportIngest> DmarcReportIngests => Set<DmarcReportIngest>();
    public DbSet<MailboxSyncRun> MailboxSyncRuns => Set<MailboxSyncRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}
