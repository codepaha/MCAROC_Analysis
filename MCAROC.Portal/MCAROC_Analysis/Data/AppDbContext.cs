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

        modelBuilder.Entity<Client>().HasData(
            new Client { ClientId = 1, ClientCode = "HDFC", ClientName = "HDFC Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 2, ClientCode = "KOTAK", ClientName = "Kotak Mahindra Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 3, ClientCode = "AUSFB", ClientName = "AU Small Finance Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) },
            new Client { ClientId = 4, ClientCode = "FEDERAL", ClientName = "Federal Bank", IsActive = true, CreatedDate = new DateTime(2026, 1, 1) }
        );
    }
}
