using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Data;

public sealed class DmarcAnalyzerDbContext(DbContextOptions<DmarcAnalyzerDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<MailboxSource> MailboxSources => Set<MailboxSource>();

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
            entity.HasIndex(x => x.DefaultClientId);

            entity.HasOne(x => x.DefaultClient)
                .WithMany()
                .HasForeignKey(x => x.DefaultClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
