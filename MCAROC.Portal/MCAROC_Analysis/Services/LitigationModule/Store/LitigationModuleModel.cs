using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Store;

/// <summary>The module's table mapping, called from <c>AppDbContext.OnModelCreating</c>. All tables are in the <c>litigation</c> schema; the module reads
/// and writes them through <c>db.Set&lt;T&gt;()</c>, so the application context exposes no <c>DbSet</c> for them.</summary>
public static class LitigationModuleModel
{
    public const string Schema = "litigation";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModuleCase>(e =>
        {
            e.ToTable("Cases", Schema);
            e.HasKey(x => x.CaseId);
            e.Property(x => x.LegacyCaseRowId).HasMaxLength(64);
            e.Property(x => x.CaseKey).HasMaxLength(256);
            e.HasIndex(x => x.LegacyLitigationCaseId).IsUnique().HasFilter("[LegacyLitigationCaseId] IS NOT NULL");
            e.HasIndex(x => new { x.LegacyJobId, x.LegacyCaseRowId }).IsUnique().HasFilter("[LegacyJobId] IS NOT NULL AND [LegacyCaseRowId] IS NOT NULL");
            e.HasIndex(x => x.CaseKey);
        });

        modelBuilder.Entity<ModuleSubject>(e =>
        {
            e.ToTable("Subjects", Schema);
            e.HasKey(x => x.SubjectId);
            e.Property(x => x.Name).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<ModuleScope>(e =>
        {
            e.ToTable("Scopes", Schema);
            e.HasKey(x => x.ScopeId);
            e.HasOne(x => x.Subject).WithMany().HasForeignKey(x => x.SubjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Kind, x.ExternalId }).IsUnique();
            // The alias counter is allocated with a concurrency check so two writers can never hand out the same alias.
            e.Property(x => x.NextAliasIndex).IsConcurrencyToken();
            e.Property(x => x.Version).IsConcurrencyToken();   // a stale tracked write must not overwrite a version bumped by another writer
        });

        modelBuilder.Entity<ModuleScopeCase>(e =>
        {
            e.ToTable("ScopeCases", Schema);
            e.HasKey(x => x.ScopeCaseId);
            e.Property(x => x.Alias).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExclusionReason).HasMaxLength(1000);
            e.HasOne(x => x.Scope).WithMany().HasForeignKey(x => x.ScopeId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ScopeId, x.CaseId }).IsUnique();
            e.HasIndex(x => new { x.ScopeId, x.Alias }).IsUnique();
        });

        modelBuilder.Entity<ModuleBridgeOutbox>(e =>
        {
            e.ToTable("BridgeOutbox", Schema);
            e.HasKey(x => x.OutboxId);
            e.Property(x => x.LastError).HasMaxLength(2000);
            // Deleting a legacy case (or its request) removes its pending events with it.
            e.HasOne(x => x.LegacyCase).WithMany().HasForeignKey(x => x.LegacyLitigationCaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.OutboxId).HasFilter("[ProcessedUtc] IS NULL AND [DeadLetteredUtc] IS NULL").HasDatabaseName("IX_BridgeOutbox_Pending");
            e.HasIndex(x => x.LegacyLitigationCaseId);
        });

        modelBuilder.Entity<ModuleCaseObservation>(e =>
        {
            e.ToTable("CaseObservations", Schema);
            e.HasKey(x => x.ObservationId);
            // The id comes from a sequence the store reads BEFORE it decides what the projection does, so the observation is inserted once, complete, and never updated.
            e.Property(x => x.ObservationId).UseSequence("CaseObservationIds", Schema);
            e.Property(x => x.AppliedAs).HasMaxLength(64);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.CaseId, x.RetrievedUtc });
        });

        modelBuilder.Entity<ModuleCaseLive>(e =>
        {
            e.ToTable("CaseLive", Schema);
            e.HasKey(x => x.CaseId);
            e.Property(x => x.HeldChange).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ModuleRequestLink>(e =>
        {
            e.ToTable("RequestLinks", Schema);
            e.HasKey(x => x.LinkId);
            e.Property(x => x.Rule).HasMaxLength(64);
            e.Property(x => x.MatchedParty).HasMaxLength(512);
            e.Property(x => x.MatchedSide).HasMaxLength(16);
            e.Property(x => x.RoleNote).HasMaxLength(500);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.RequestId, x.CaseId }).IsUnique();
        });
    }
}
