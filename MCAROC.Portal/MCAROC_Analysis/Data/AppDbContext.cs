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
    public DbSet<EpfoEstablishment> EpfoEstablishments => Set<EpfoEstablishment>();
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

    // Phase 6
    public DbSet<CompanyStructure> CompanyStructures => Set<CompanyStructure>();
    public DbSet<ShareholdingPatternRow> ShareholdingPatternRows => Set<ShareholdingPatternRow>();
    public DbSet<CompanyNameHistory> CompanyNameHistories => Set<CompanyNameHistory>();
    public DbSet<PrincipalBusinessActivity> PrincipalBusinessActivities => Set<PrincipalBusinessActivity>();
    public DbSet<RelatedCorporate> RelatedCorporates => Set<RelatedCorporate>();
    public DbSet<RelatedPartyTransaction> RelatedPartyTransactions => Set<RelatedPartyTransaction>();
    public DbSet<CreditRating> CreditRatings => Set<CreditRating>();
    public DbSet<FinancialDisputeCase> FinancialDisputeCases => Set<FinancialDisputeCase>();
    public DbSet<ComplianceRecord> ComplianceRecords => Set<ComplianceRecord>();
    public DbSet<FinancialParameter> FinancialParameters => Set<FinancialParameter>();
    public DbSet<SecurityAllotment> SecurityAllotments => Set<SecurityAllotment>();
    public DbSet<ProprietorshipAssociation> ProprietorshipAssociations => Set<ProprietorshipAssociation>();
    public DbSet<DirectorAssignmentHistory> DirectorAssignmentHistories => Set<DirectorAssignmentHistory>();
    public DbSet<PeerComparisonMetric> PeerComparisonMetrics => Set<PeerComparisonMetric>();
    public DbSet<PeerCompany> PeerCompanies => Set<PeerCompany>();
    public DbSet<ChargeSecurityComponent> ChargeSecurityComponents => Set<ChargeSecurityComponent>();

    // Phase 7.0 — raw source-row staging (Layer 0) + completeness of the typed layer
    public DbSet<SourceRow> SourceRows => Set<SourceRow>();
    public DbSet<FinancialFact> FinancialFacts => Set<FinancialFact>();
    public DbSet<CompanyOfficer> CompanyOfficers => Set<CompanyOfficer>();
    public DbSet<CompanyEmail> CompanyEmails => Set<CompanyEmail>();
    public DbSet<PreLoginReportJob> PreLoginReportJobs => Set<PreLoginReportJob>();

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

        modelBuilder.Entity<PreLoginReportJob>(e =>
        {
            e.HasKey(x => x.PreLoginReportJobId);
            e.HasIndex(x => new { x.BatchId, x.CreatedUtc });
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.Property(x => x.Format).HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Cin).HasMaxLength(30);
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
            e.Property(x => x.AbsentOptionalSheetsJson).HasDefaultValue("[]");
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

        modelBuilder.Entity<CompanyEmail>(e =>
        {
            e.HasKey(x => x.CompanyEmailId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.EmailAddress).HasMaxLength(320);
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
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.FinancialYear, x.Basis });
            e.Property(x => x.Basis).HasConversion<string>().HasMaxLength(15);
        });

        modelBuilder.Entity<RocCharge>(e =>
        {
            e.HasKey(x => x.ChargeId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.RocChargeNumber });
            e.Property(x => x.LatestPrimaryFacilityType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LatestArrangement).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LatestSecurityConfidence).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<RocChargeEvent>(e =>
        {
            e.HasKey(x => x.ChargeEventId);
            e.HasOne(x => x.RocCharge).WithMany(c => c.Events).HasForeignKey(x => x.RocChargeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.EventType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MatchConfidence).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PrimaryFacilityType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Arrangement).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SecurityClassificationConfidence).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<ChargeSecurityComponent>(e =>
        {
            e.HasKey(x => x.ChargeSecurityComponentId);
            e.HasOne(x => x.RocChargeEvent).WithMany(ev => ev.SecurityComponents).HasForeignKey(x => x.RocChargeEventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.SecurityType).HasConversion<string>().HasMaxLength(25);
            e.Property(x => x.Ranking).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(10);
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

        modelBuilder.Entity<EpfoEstablishment>(e =>
        {
            e.HasKey(x => x.EpfoEstablishmentId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.EstablishmentId).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.City).HasMaxLength(120);
            e.Property(x => x.WorkingStatus).HasMaxLength(60);
            e.Property(x => x.LatestWageMonth).HasMaxLength(30);
            // ExemptionStatus is a multi-line block (PF / Pension / EDLI), Address + Flags are free text — left nvarchar(max).
        });

        modelBuilder.Entity<AuditorObservation>(e =>
        {
            e.HasKey(x => x.ObservationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.Basis });
            e.Property(x => x.Basis).HasConversion<string>().HasMaxLength(15);
        });

        modelBuilder.Entity<Litigation>(e =>
        {
            e.HasKey(x => x.LitigationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20).HasDefaultValue(LitigationSource.RocReport);
        });

        // ── Phase 6 domain data ──
        modelBuilder.Entity<CompanyStructure>(e =>
        {
            e.HasKey(x => x.CompanyStructureId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<ShareholdingPatternRow>(e =>
        {
            e.HasKey(x => x.ShareholdingPatternRowId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.HolderClass).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.CategoryGroup).HasMaxLength(200);
            e.Property(x => x.EquityPercent).HasPrecision(9, 4);
            e.Property(x => x.PreferencePercent).HasPrecision(9, 4);
        });

        modelBuilder.Entity<CompanyNameHistory>(e =>
        {
            e.HasKey(x => x.CompanyNameHistoryId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.PreviousName).HasMaxLength(400);
        });

        modelBuilder.Entity<PrincipalBusinessActivity>(e =>
        {
            e.HasKey(x => x.PrincipalBusinessActivityId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.MainActivityGroupCode).HasMaxLength(20);
            e.Property(x => x.BusinessActivityCode).HasMaxLength(20);
            e.Property(x => x.TurnoverPercent).HasPrecision(9, 4);
        });

        modelBuilder.Entity<RelatedCorporate>(e =>
        {
            e.HasKey(x => x.RelatedCorporateId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.RelationshipType).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<ComplianceRecord>(e =>
        {
            e.HasKey(x => x.ComplianceRecordId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.RecordType).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<FinancialParameter>(e =>
        {
            e.HasKey(x => x.FinancialParameterId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<SecurityAllotment>(e =>
        {
            e.HasKey(x => x.SecurityAllotmentId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<ProprietorshipAssociation>(e =>
        {
            e.HasKey(x => x.ProprietorshipAssociationId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<DirectorAssignmentHistory>(e =>
        {
            e.HasKey(x => x.DirectorAssignmentHistoryId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
        });

        modelBuilder.Entity<PeerComparisonMetric>(e =>
        {
            e.HasKey(x => x.PeerComparisonMetricId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.Position).HasConversion<string>().HasMaxLength(15);
        });

        modelBuilder.Entity<PeerCompany>(e =>
        {
            e.HasKey(x => x.PeerCompanyId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
            e.Property(x => x.LegalName).HasMaxLength(300);
            e.Property(x => x.Cin).HasMaxLength(25);
            e.Property(x => x.City).HasMaxLength(120);
        });

        modelBuilder.Entity<SourceRow>(e =>
        {
            e.HasKey(x => x.SourceRowId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.WorkbookRole, x.SheetName });
            // Layer 0 is immutable and append-only: exactly one record per physical (run, workbook,
            // sheet, row). A retry or bug that tried to re-insert a raw row is rejected by the DB.
            e.HasIndex(x => new { x.IngestionRunId, x.WorkbookRole, x.SheetIndex, x.RowNumber }).IsUnique();
            e.Property(x => x.WorkbookRole).HasMaxLength(20);
            e.Property(x => x.SheetName).HasMaxLength(255);
            e.Property(x => x.RowHash).HasMaxLength(64).IsFixedLength();
        });

        modelBuilder.Entity<FinancialFact>(e =>
        {
            e.HasKey(x => x.FinancialFactId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId, x.Basis });
            e.Property(x => x.Basis).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.Section).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.Label).HasMaxLength(300);
        });

        modelBuilder.Entity<CompanyOfficer>(e =>
        {
            e.HasKey(x => x.CompanyOfficerId);
            e.HasIndex(x => new { x.RequestId, x.IngestionRunId });
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
            // One session per request (Phase 4 v1 — see ChatSession doc comment). Unique so a
            // concurrent/double-submitted first question can't create two sessions and split the thread.
            e.HasIndex(x => x.RequestId).IsUnique();
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.ChatMessageId);
            e.HasOne<ChatSession>().WithMany(s => s.Messages).HasForeignKey(x => x.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ChatSessionId);
            e.HasIndex(x => new { x.ChatSessionId, x.ClientTurnId }).IsUnique().HasFilter("[ClientTurnId] IS NOT NULL");
            e.HasIndex(x => x.InReplyToChatMessageId);
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
