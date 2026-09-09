using Microsoft.EntityFrameworkCore;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<McaRequest> Requests => Set<McaRequest>();
    public DbSet<RequestDocument> RequestDocuments => Set<RequestDocument>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<IngestionIssue> IngestionIssues => Set<IngestionIssue>();

    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<Director> Directors => Set<Director>();
    public DbSet<DirectorAssociation> DirectorAssociations => Set<DirectorAssociation>();
    public DbSet<Shareholding> Shareholdings => Set<Shareholding>();
    public DbSet<FinancialYearData> FinancialYearData => Set<FinancialYearData>();

    public DbSet<RocCharge> RocCharges => Set<RocCharge>();
    public DbSet<RocChargeEvent> RocChargeEvents => Set<RocChargeEvent>();

    public DbSet<MsmePayment> MsmePayments => Set<MsmePayment>();
    public DbSet<GstRegistration> GstRegistrations => Set<GstRegistration>();
    public DbSet<GstFiling> GstFilings => Set<GstFiling>();
    public DbSet<EpfoContribution> EpfoContributions => Set<EpfoContribution>();
    public DbSet<AuditorObservation> AuditorObservations => Set<AuditorObservation>();
    public DbSet<Litigation> Litigations => Set<Litigation>();

    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();
    public DbSet<AnalysisFinding> AnalysisFindings => Set<AnalysisFinding>();

    public DbSet<McaFilingBatch> McaFilingBatches => Set<McaFilingBatch>();
    public DbSet<McaFiling> McaFilings => Set<McaFiling>();
    public DbSet<McaFilingDocument> McaFilingDocuments => Set<McaFilingDocument>();
    public DbSet<McaFilingExtraction> McaFilingExtractions => Set<McaFilingExtraction>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Default precision for monetary/count decimals (mostly Rs. Crore values); percentages override below.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(e =>
        {
            e.HasKey(x => x.ClientId);
            e.HasIndex(x => x.ClientCode).IsUnique();
        });

        modelBuilder.Entity<McaRequest>(e =>
        {
            e.HasKey(x => x.RequestId);
            e.HasIndex(x => x.RequestNumber).IsUnique();
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.EntityType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RequestStatus).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<RequestDocument>(e =>
        {
            e.HasKey(x => x.DocumentId);
            e.HasOne(x => x.Request).WithMany(r => r.Documents).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.UploadStatus).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<IngestionRun>(e =>
        {
            e.HasKey(x => x.IngestionRunId);
            e.HasOne(x => x.Request).WithMany(r => r.IngestionRuns).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.RunNumber }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<IngestionIssue>(e =>
        {
            e.HasKey(x => x.IssueId);
            e.HasOne(x => x.IngestionRun).WithMany(r => r.Issues).HasForeignKey(x => x.IngestionRunId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<CompanyProfile>(e =>
        {
            e.HasKey(x => x.CompanyProfileId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<Director>(e =>
        {
            e.HasKey(x => x.DirectorId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<DirectorAssociation>(e =>
        {
            e.HasKey(x => x.AssociationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<Shareholding>(e =>
        {
            e.HasKey(x => x.ShareholdingId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.HoldingPercentage).HasPrecision(9, 4);
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<FinancialYearData>(e =>
        {
            e.HasKey(x => x.FinancialId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.FinancialYear });
        });

        modelBuilder.Entity<RocCharge>(e =>
        {
            e.HasKey(x => x.ChargeId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.RocChargeNumber });
        });

        modelBuilder.Entity<RocChargeEvent>(e =>
        {
            e.HasKey(x => x.ChargeEventId);
            e.HasOne(x => x.RocCharge).WithMany(c => c.Events).HasForeignKey(x => x.RocChargeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.EventType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MatchConfidence).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<MsmePayment>(e =>
        {
            e.HasKey(x => x.MsmeId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<GstRegistration>(e =>
        {
            e.HasKey(x => x.GstId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.Gstin });
        });

        modelBuilder.Entity<GstFiling>(e =>
        {
            e.HasKey(x => x.GstFilingId);
            e.HasOne<GstRegistration>().WithMany(r => r.Filings).HasForeignKey(x => x.GstRegistrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<EpfoContribution>(e =>
        {
            e.HasKey(x => x.EpfoId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<AuditorObservation>(e =>
        {
            e.HasKey(x => x.ObservationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<Litigation>(e =>
        {
            e.HasKey(x => x.LitigationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<AnalysisRun>(e =>
        {
            e.HasKey(x => x.AnalysisRunId);
            e.HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.RunNumber }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.OverallReviewPriority).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<AnalysisFinding>(e =>
        {
            e.HasKey(x => x.FindingId);
            e.HasOne<AnalysisRun>().WithMany(r => r.Findings).HasForeignKey(x => x.AnalysisRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AnalysisRunId, x.Section });
            e.HasIndex(x => new { x.RequestId, x.Code });
            e.Property(x => x.Section).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.TemporalStatus).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Code).HasMaxLength(80);
        });

        modelBuilder.Entity<McaFilingBatch>(e =>
        {
            e.HasKey(x => x.BatchId);
            e.HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<McaFiling>(e =>
        {
            e.HasKey(x => x.FilingId);
            e.HasOne(x => x.Batch).WithMany(b => b.Filings).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BatchId, x.Srn });
        });

        modelBuilder.Entity<McaFilingDocument>(e =>
        {
            e.HasKey(x => x.FilingDocumentId);
            e.HasOne(x => x.Filing).WithMany(f => f.Documents).HasForeignKey(x => x.FilingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<McaFilingDocument>().WithMany().HasForeignKey(x => x.DuplicateOfDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BatchId, x.FileHash });
            e.HasIndex(x => x.ProcessingStatus);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ClassificationConfidence).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.TextExtractionMethod).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.AiExtractionStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ChunkingStatus).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<McaFilingExtraction>(e =>
        {
            e.HasKey(x => x.ExtractionId);
            e.HasOne(x => x.Filing).WithMany().HasForeignKey(x => x.FilingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FilingDocument).WithMany().HasForeignKey(x => x.FilingDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ValidationStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            // Defense-in-depth against a duplicate paid Gemini call slipping past the atomic claim in
            // ExtractFilingAsync (e.g. under a weaker isolation level than assumed): the DB itself refuses
            // a second extraction row for the same filing outright, rather than relying solely on
            // application-level locking to prevent one.
            e.HasIndex(x => x.FilingId).IsUnique().HasFilter("[FilingId] IS NOT NULL");
        });

        modelBuilder.Entity<DocumentChunk>(e =>
        {
            e.HasKey(x => x.ChunkId);
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.FilingDocumentId);
            e.HasIndex(x => x.FilingId);
            e.HasIndex(x => new { x.RequestId, x.Category });
            e.HasIndex(x => new { x.RequestId, x.FormType });
            e.HasIndex(x => new { x.RequestId, x.Srn });
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            // 768-dim vector chosen for gemini-embedding-001's recommended truncation size (see
            // EmbeddingService) — every chunk in the table must use this same dimensionality since
            // VECTOR_DISTANCE requires both operands to match.
            e.Property(x => x.Embedding).HasColumnType("vector(768)");
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.HasKey(x => x.ChatSessionId);
            e.HasOne<McaRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RequestId);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.ChatMessageId);
            e.HasOne<ChatSession>().WithMany(s => s.Messages).HasForeignKey(x => x.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ChatSessionId);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<Client>().HasData(
            new Client { ClientId = 1, ClientCode = "HDFC", ClientName = "HDFC Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 2, ClientCode = "KOTAK", ClientName = "Kotak Mahindra Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 3, ClientCode = "AUSFB", ClientName = "AU Small Finance Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 4, ClientCode = "FEDERAL", ClientName = "Federal Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) }
        );
    }
}
