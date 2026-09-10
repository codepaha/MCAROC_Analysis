using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Seeds one comprehensive analyzed request into the test DB — enough of the entity graph to
/// exercise every <see cref="MCAROC_Analysis.Models.RequestDetailsViewModel"/> computed property and,
/// later, the <c>DossierAssembler</c>. Deterministic values so a golden-master snapshot is stable.</summary>
public static class DossierTestSeed
{
    public static async Task<(long RequestId, long IngestionRunId, long AnalysisRunId)> SeedAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"D{Guid.NewGuid():N}"[..10], ClientName = "Test Bank", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Golden Master Ltd",
            Cin = "U12345KA2000PLC000001", Pan = "AAAAA0000A",
            RequestNumber = $"GM-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var ing = new IngestionRun
        {
            RequestId = request.RequestId, RunNumber = 1, StartedDate = request.CreatedDate,
            CompletedDate = request.CreatedDate.AddMinutes(2), Status = IngestionRunStatus.CompletedClean
        };
        db.IngestionRuns.Add(ing);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = ing.IngestionRunId;

        long R = request.RequestId, I = ing.IngestionRunId;
        E Tag<E>(E e) where E : ExtractedEntityBase { e.RequestId = R; e.IngestionRunId = I; return e; }

        db.CompanyProfiles.Add(Tag(new CompanyProfile
        {
            CompanyName = "Golden Master Ltd", Cin = request.Cin, Pan = request.Pan, CompanyStatus = "Active",
            IncorporationDate = new DateOnly(2000, 6, 1), AuthorisedCapital = 100m, PaidUpCapital = 80m
        }));

        // Directors — 2 current, 1 ceased.
        db.Directors.AddRange(
            Tag(new Director { Din = "00000001", NameRaw = "ALICE RAO", NameNormalized = "ALICE RAO", Designation = "Managing Director", OriginalAppointmentDate = new DateOnly(2015, 4, 1) }),
            Tag(new Director { Din = "00000002", NameRaw = "BHARAT SEN", NameNormalized = "BHARAT SEN", Designation = "Director", OriginalAppointmentDate = new DateOnly(2019, 9, 1) }),
            Tag(new Director { Din = "00000003", NameRaw = "CHITRA IYER", NameNormalized = "CHITRA IYER", Designation = "Director", OriginalAppointmentDate = new DateOnly(2013, 4, 1), CessationDate = new DateOnly(2026, 8, 10) }));
        db.CompanyOfficers.Add(Tag(new CompanyOfficer { NameRaw = "DEV MENON", NameNormalized = "DEV MENON", Designation = "Company Secretary", DinCellRaw = "-" }));

        // Financial years — standalone 2023/2024/2025, consolidated 2024/2025; one CF-inferred.
        db.FinancialYearData.AddRange(
            Tag(new FinancialYearData { FinancialYear = 2023, Basis = FinancialBasis.Standalone, Revenue = 400m, NetWorth = 120m, Pat = 10m, TotalDebt = 300m }),
            Tag(new FinancialYearData { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Revenue = 360m, NetWorth = 90m, Pat = -5m, TotalDebt = 340m }),
            Tag(new FinancialYearData { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Revenue = 300m, NetWorth = -40m, Pat = -50m, TotalDebt = 380m, Cfo = -12m, CashFlowYearInferred = true }),
            Tag(new FinancialYearData { FinancialYear = 2024, Basis = FinancialBasis.Consolidated, Revenue = 500m, NetWorth = 110m }),
            Tag(new FinancialYearData { FinancialYear = 2025, Basis = FinancialBasis.Consolidated, Revenue = 420m, NetWorth = -18m }));

        db.FinancialFacts.AddRange(
            Tag(new FinancialFact { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.BalanceSheet, Label = "Reserves and Surplus", FinancialYear = 2025, RawValue = "-186.2", NumericValue = -186.2m }),
            Tag(new FinancialFact { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Debt / Equity Ratio", FinancialYear = 2025, RawValue = "3.44", NumericValue = 3.44m }));

        // Charges — 2 open (C1 modified + classified, C2 single event), 1 satisfied.
        var c1 = Tag(new RocCharge
        {
            RocChargeNumber = "C1", LatestChargeHolderRaw = "STATE BANK OF INDIA", LatestChargeHolderNormalized = "STATE BANK OF INDIA",
            ChargeStatus = "Open", CreationDate = new DateOnly(2016, 3, 18), CurrentAmount = 610m,
            LatestModificationDate = new DateOnly(2022, 11, 29),
            LatestSecurityConfidence = ChargeClassificationConfidence.High,
            LatestSecurityTypesJson = JsonSerializer.Serialize(new[] { "CurrentAssets", "BookDebts" }),
            LatestPrimaryFacilityType = FacilityType.WorkingCapital, LatestArrangement = ChargeArrangement.Consortium
        });
        c1.Events.Add(Tag(new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2016, 3, 18), ChargeAmount = 380m, HolderNameRaw = "STATE BANK OF INDIA" }));
        c1.Events.Add(Tag(new RocChargeEvent { EventType = ChargeEventType.Modification, EventDate = new DateOnly(2022, 11, 29), ChargeAmount = 610m, HolderNameRaw = "STATE BANK OF INDIA" }));

        var c2 = Tag(new RocCharge
        {
            RocChargeNumber = "C2", LatestChargeHolderRaw = "HDFC BANK LIMITED", LatestChargeHolderNormalized = "HDFC BANK LIMITED",
            ChargeStatus = "Open", CreationDate = new DateOnly(2014, 1, 5), CurrentAmount = 400m
        });
        c2.Events.Add(Tag(new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2014, 1, 5), ChargeAmount = 400m, HolderNameRaw = "HDFC BANK LIMITED" }));

        var c3 = Tag(new RocCharge
        {
            RocChargeNumber = "C3", LatestChargeHolderRaw = "ICICI BANK LIMITED", LatestChargeHolderNormalized = "ICICI BANK LIMITED",
            ChargeStatus = "Satisfied", CreationDate = new DateOnly(2009, 2, 14), CurrentAmount = 5m,
            SatisfactionDate = new DateOnly(2018, 6, 19)
        });
        c3.Events.Add(Tag(new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2009, 2, 14), ChargeAmount = 5m, HolderNameRaw = "ICICI BANK LIMITED" }));
        c3.Events.Add(Tag(new RocChargeEvent { EventType = ChargeEventType.Satisfaction, EventDate = new DateOnly(2018, 6, 19), HolderNameRaw = "ICICI BANK LIMITED" }));
        db.RocCharges.AddRange(c1, c2, c3);

        // Litigation — one against, one role-uncertain.
        var lit1 = Tag(new Litigation { CaseType = "Filed Against this Corporate", CaseStatus = "Pending", CaseCategory = "Insolvency", Court = "NCLT Bengaluru", Litigants = "Axis Bank Limited vs Golden Master Ltd", CaseNumber = "CP(IB) 214/2019", MatchStatus = LitigationMatchStatus.Confirmed });
        var lit2 = Tag(new Litigation { CaseType = "Unknown", CaseStatus = "Pending", CaseCategory = "Civil", Court = "City Civil Court", Litigants = "Unclear parties description", CaseNumber = "OS 77/2021", MatchStatus = LitigationMatchStatus.Confirmed });
        db.Litigations.AddRange(lit1, lit2);

        db.MsmePayments.Add(Tag(new MsmePayment { ReportingPeriod = "2025-H1", SupplierNameRaw = "Acme Supplies", AmountDueCrore = 2.5m }));
        var gst = Tag(new GstRegistration { Gstin = "29AAAAA0000A1Z5", State = "Karnataka", Status = "Active", RegistrationDate = new DateOnly(2017, 7, 1) });
        gst.Filings.Add(Tag(new GstFiling { Gstin = gst.Gstin, ReturnType = "GSTR3B", TaxPeriod = "Jun", DueDate = new DateOnly(2025, 7, 20), FilingDate = new DateOnly(2025, 7, 25), DelayDays = 5, FilingStatus = "Filed" }));
        db.GstRegistrations.Add(gst);
        db.EpfoContributions.Add(Tag(new EpfoContribution { EstablishmentId = "KABLR0001", EstablishmentName = "Golden Master Ltd", WageMonth = "Jun, 2025", EmployeeCount = 200, PaymentStatus = "Paid on Time" }));
        db.AuditorObservations.Add(Tag(new AuditorObservation { FinancialYear = 2025, Basis = FinancialBasis.Standalone, HasQualificationOrAdverseRemark = true, ObservationText = "Material uncertainty on going concern." }));
        db.ComplianceRecords.AddRange(
            Tag(new ComplianceRecord { RecordType = ComplianceRecordType.Cdr, Description = "Debt restructured under CDR", RecordDate = new DateOnly(2014, 4, 28) }),
            Tag(new ComplianceRecord { RecordType = ComplianceRecordType.SuitFiled, Bank = "IDBI BANK", AmountCrore = 12m, DefaulterType = "Defaulter - Suit Filed", RecordDate = new DateOnly(2014, 3, 31) }),
            Tag(new ComplianceRecord { RecordType = ComplianceRecordType.SuitFiled, Bank = "IDBI BANK", AmountCrore = 12m, DefaulterType = "Defaulter - Suit Filed", RecordDate = new DateOnly(2014, 6, 30) }));

        // Layer-0 source rows — two workbooks / a few sheets, incl. a wide row with a long unclipped cell.
        db.SourceRows.AddRange(
            SR(R, I, "RocReport", "Company Information", 0, 1, "Field", "Value"),
            SR(R, I, "RocReport", "Company Information", 0, 2, "CIN", "U12345KA2000PLC000001"),
            SR(R, I, "RocReport", "Company Information", 0, 3, "Company Name", "Golden Master Ltd"),
            SR(R, I, "RocReport", "Directors", 1, 1, "Name", "DIN", "Designation"),
            SR(R, I, "RocReport", "Directors", 1, 2, "ALICE RAO", "00000001", "Managing Director"),
            SR(R, I, "RocReport", "Directors", 1, 3, "DEV MENON", "-", "Company Secretary"),
            SR(R, I, "ChargeReport", "Charges", 0, 1, "Charge ID", "Holder", "Amount", "Property particulars"),
            SR(R, I, "ChargeReport", "Charges", 0, 2, "C1", "STATE BANK OF INDIA", "610",
                "First pari passu charge on the entire current assets and movable fixed assets of the company " +
                "both present and future, along with the other working-capital consortium bankers, ranking pari passu inter se."));

        await db.SaveChangesAsync();

        // Analysis run + findings (with SourceReferenceJson for the per-case / per-charge attribution).
        var an = new AnalysisRun
        {
            RequestId = R, IngestionRunId = I, RunNumber = 1, Status = AnalysisRunStatus.Completed,
            StartedDate = request.CreatedDate.AddMinutes(3), CompletedDate = request.CreatedDate.AddMinutes(5),
            OverallReviewPriority = ReviewPriority.High,
            CriticalFindingsCount = 1, ReviewFindingsCount = 2, WatchFindingsCount = 1, PositiveFindingsCount = 0
        };
        db.AnalysisRuns.Add(an);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = I;

        string Ref(string type, params long[] ids) => JsonSerializer.Serialize(new { entityType = type, entityIds = ids });

        db.AnalysisFindings.AddRange(
            F(an, R, FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_NET_WORTH_NEGATIVE", "Negative net worth", 90),
            F(an, R, FindingSection.Litigation, FindingSeverity.Review, TemporalStatus.Current, LitigationRules.PendingAgainstCompanyCode, "Pending Litigation Against Company", 80, Ref(nameof(Litigation), lit1.LitigationId)),
            F(an, R, FindingSection.Litigation, FindingSeverity.Watch, TemporalStatus.Current, LitigationRules.RoleUncertainCode, "Potential Litigation Requiring Role Verification", 40, Ref(nameof(Litigation), lit2.LitigationId)),
            F(an, R, FindingSection.Charges, FindingSeverity.Review, TemporalStatus.Current, ChargeRules.MaterialEnhancementCode, "Material Charge Enhancement", 70, Ref(nameof(RocCharge), c1.ChargeId)));
        await db.SaveChangesAsync();

        return (R, I, an.AnalysisRunId);
    }

    private static SourceRow SR(long requestId, long ingestionRunId, string workbookRole, string sheetName,
        int sheetIndex, int rowNumber, params string?[] cells)
    {
        var json = JsonSerializer.Serialize(cells);
        return new SourceRow
        {
            RequestId = requestId, IngestionRunId = ingestionRunId, SourceDocumentId = 0,
            WorkbookRole = workbookRole, SheetName = sheetName, SheetIndex = sheetIndex, RowNumber = rowNumber,
            CellsJson = json,
            RowHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))),
            ExtractedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static AnalysisFinding F(AnalysisRun run, long requestId, FindingSection section, FindingSeverity sev,
        TemporalStatus temporal, string code, string title, int displayPriority, string? sourceRef = null) => new()
    {
        AnalysisRunId = run.AnalysisRunId, RequestId = requestId, Section = section, Severity = sev,
        TemporalStatus = temporal, Code = code, Title = title, SummaryText = title + ".",
        DisplayPriority = displayPriority, ObservationDate = new DateOnly(2026, 1, 1), SourceReferenceJson = sourceRef
    };
}
