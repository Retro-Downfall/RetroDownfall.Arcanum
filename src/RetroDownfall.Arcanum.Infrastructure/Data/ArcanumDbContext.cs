using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

[UnconditionalSuppressMessage(
    "AOT",
    "IL3050",
    Justification = "DbContext base ctor is marked RequiresDynamicCode; Arcanum always applies UseModel(ArcanumDbContextModel.Instance) so the compiled model is used at runtime without EF design-time model materialization.")]
[UnconditionalSuppressMessage(
    "AOT",
    "IL2026",
    Justification = "DbContext base ctor is marked RequiresUnreferencedCode; Arcanum uses a compiled EF model (UseModel), trim-safe entity registrations, and no dynamic LINQ—see https://aka.ms/efcore-docs-trimming.")]
[ExcludeFromCodeCoverage] // Reason: EF Core model surface; repositories and migrations are tested separately.
public sealed class ArcanumDbContext(
    DbContextOptions<ArcanumDbContext> options,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : DbContext(options)
{
    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Entry> Entries => Set<Entry>();

    public DbSet<MageSetting> MageSettings => Set<MageSetting>();

    public DbSet<WorkspaceContext> WorkspaceContexts => Set<WorkspaceContext>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<Prompt> Prompts => Set<Prompt>();

    public DbSet<Apprentice> Apprentices => Set<Apprentice>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = secretStore;

        optionsBuilder.AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);

        if (optionsBuilder.IsConfigured)
        {
            return;
        }

        string dbPath = ArcanumPaths.GrimoireDatabaseFile;
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphraseSource.Passphrase,
        }.ToString();
        optionsBuilder.UseSqlite(connectionString);
        optionsBuilder.UseModel(ArcanumDbContextModel.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired().HasDefaultValue("active");
            entity.Property(e => e.Summary);
            entity.Property(e => e.LastSummarizedMessageAt);
            entity.Property(e => e.TotalTokensUsed).HasDefaultValue(0L);
            entity.Property(e => e.TotalCostUsd).HasDefaultValue(0m).HasPrecision(18, 8);
            entity.Property(e => e.UnsummarizedEntryCount).HasDefaultValue(0);
            entity.Property(e => e.ForkedFromSessionId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.UpdatedAt);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CampaignId);
            entity.HasIndex(e => new { e.Status, e.UpdatedAt });
            entity.HasIndex(e => new { e.CampaignId, e.Status, e.UpdatedAt });
            entity.HasIndex(e => e.UnsummarizedEntryCount);
            entity.HasIndex(e => e.ForkedFromSessionId);
            entity.HasMany(e => e.Entries)
                .WithOne(m => m.Session!)
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Entry>(entity =>
        {
            entity.ToTable("Entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.ModelUsed).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Role).HasConversion<int>();
            entity.Property(e => e.ToolCallId).HasMaxLength(256);
            entity.Property(e => e.ToolName).HasMaxLength(256);
            entity.Property(e => e.IsPinned).HasDefaultValue(false);
            entity.HasIndex(m => new { m.SessionId, m.CreatedAt });
            entity.HasIndex(m => m.Role);
            entity.HasIndex(m => new { m.SessionId, m.IsPinned });
        });

        modelBuilder.Entity<MageSetting>(entity =>
        {
            entity.ToTable("MageSettings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Value).IsRequired();
        });

        modelBuilder.Entity<WorkspaceContext>(entity =>
        {
            entity.ToTable("WorkspaceContexts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.WorkspacePath)
                .HasColumnName("RootPath")
                .HasMaxLength(4096)
                .IsRequired();
            entity.Property(e => e.SerializedSnapshot).IsRequired();
            entity.HasIndex(e => new { e.WorkspacePath, e.CreatedAt });
        });

        modelBuilder.Entity<Campaign>(entity =>
        {
            entity.ToTable("Campaigns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.NameLower).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => e.NameLower).IsUnique();
            entity.Property(e => e.Path).HasMaxLength(4096).IsRequired();
            entity.HasIndex(e => e.Path).IsUnique();
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Description).HasMaxLength(2048);
            entity.Property(e => e.Settings).IsRequired();
            entity.Property(e => e.SanctumConfigJson).IsRequired().HasDefaultValue("{}");
            entity.HasIndex(e => e.Type);
        });

        modelBuilder.Entity<Prompt>(entity =>
        {
            entity.ToTable("Prompts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2048);
            entity.Property(e => e.Tags).IsRequired();
            entity.Property(e => e.Template).IsRequired();
            entity.Property(e => e.ParameterSchema);
            entity.Property(e => e.DefaultParameters);
            entity.Property(e => e.Model).HasMaxLength(256);
            entity.Property(e => e.Provider).HasMaxLength(256);
            entity.HasOne(e => e.Campaign)
                .WithMany()
                .HasForeignKey(e => e.CampaignId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.CampaignId, e.Name });
        });

        modelBuilder.Entity<Apprentice>(entity =>
        {
            entity.ToTable("Apprentices");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Goal).IsRequired();
            entity.Property(e => e.Plan).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.WorkspacePath).HasMaxLength(4096).IsRequired();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CampaignId);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.UpdatedAt);
            entity.HasOne<Campaign>()
                .WithMany()
                .HasForeignKey(e => e.CampaignId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Session>()
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
