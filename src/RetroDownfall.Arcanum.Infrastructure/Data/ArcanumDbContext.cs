using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
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

public sealed class ArcanumDbContext(
    DbContextOptions<ArcanumDbContext> options,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<MageSetting> MageSettings => Set<MageSetting>();
    public DbSet<WorkspaceContext> WorkspaceContexts => Set<WorkspaceContext>();
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = secretStore;
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
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasMany(e => e.Messages)
                .WithOne(m => m.Conversation!)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.ToTable("ChatMessages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.ModelUsed).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Role).HasConversion<int>();
            entity.HasIndex(e => e.ConversationId);
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
            entity.Property(e => e.RootPath).HasMaxLength(4096).IsRequired();
            entity.Property(e => e.ProjectSummary).IsRequired();
            entity.HasIndex(e => e.RootPath);
        });
    }
}
