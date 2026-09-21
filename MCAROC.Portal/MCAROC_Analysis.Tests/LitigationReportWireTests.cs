using System.Globalization;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationReportWireTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static LitigationController CreateLitigationController(AppDbContext db)
    {
        var env = new FakeEnv(Path.GetTempPath());
        var controller = new LitigationController(
            db: db,
            env: env,
            analysis: null!,
            searchJobService: null!,
            searchQueue: null!,
            bprOptions: Options.Create(new BprLitigationOptions()),
            logger: NullLogger<LitigationController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    // ── 1. Large-Snapshot Scale Test (> 2,100 cases with subqueries) ─────────

    [Fact]
    public async Task AssembleAsync_LargeSnapshot_SubqueriesExecuteWithoutParameterLimitOverflow()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LG" + Guid.NewGuid().ToString("N")[..6], ClientName = "Large Scale Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Large Scale Co", RequestNumber = $"REQ-LG-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Large Scale Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-lg-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-lg-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        // Seed 2,200 cases and orders (exceeding SQL Server's 2,100 parameter limit)
        const int count = 2200;
        var cases = new List<LitigationCase>(count);
        for (int i = 0; i < count; i++)
        {
            cases.Add(new LitigationCase
            {
                RequestId = request.RequestId,
                CaseNumber = $"CASE-LG-{i:D5}",
                Court = i % 3 == 0 ? "High Court of Delhi" : i % 3 == 1 ? "NCLT Delhi" : "District Court Saket",
                CourtCategory = i % 3 == 0 ? "high_court" : i % 3 == 1 ? "tribunal" : "district_court",
                CaseStatus = i % 2 == 0 ? "Pending" : "Disposed",
                CaseStage = i % 2 == 0 ? "Hearing Stage" : "Final Disposal",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            });
        }
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();

        var sourceReports = new List<LitigationCaseSourceReport>(count);
        var caseOrders = new List<LitigationCaseOrder>(count);
        for (int i = 0; i < count; i++)
        {
            sourceReports.Add(new LitigationCaseSourceReport
            {
                LitigationCaseId = cases[i].LitigationCaseId,
                LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
                FirstSeenUtc = DateTime.UtcNow
            });
            caseOrders.Add(new LitigationCaseOrder
            {
                LitigationCaseId = cases[i].LitigationCaseId,
                OrderDate = "2025-01-10",
                OrderType = "Daily Order",
                PdfUrl = $"https://vendor.internal/order_{i}.pdf",
                CreatedUtc = DateTime.UtcNow
            });
        }
        db.LitigationCaseSourceReports.AddRange(sourceReports);
        db.LitigationCaseOrders.AddRange(caseOrders);
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);

        Assert.NotNull(report);
        Assert.Equal(count, report.Cases.Count);
        Assert.Equal(count, report.CourtSummaryGrid.TotalCases);
        Assert.True(report.CourtSummaryGrid.IsReconciled);
        Assert.Equal(count, report.CourtSummaryGrid.TotalOrders);
    }

    // ── 2. Multi-Protocol Zero-Leakage Test ──────────────────────────────────

    [Fact]
    public async Task AssembleAsync_MultiProtocolUrls_NeverLeakedIntoReportPdfOrCsv()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LK" + Guid.NewGuid().ToString("N")[..6], ClientName = "Leakage Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Leakage Co", RequestNumber = $"REQ-LK-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Leakage Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-lk-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-lk-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var c = new LitigationCase
        {
            RequestId = request.RequestId,
            CaseNumber = "CASE-LK-001",
            Court = "Bombay HC",
            CourtCategory = "high_court",
            CaseStatus = "Pending",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = c.LitigationCaseId,
            LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow
        });

        var sensitiveUrls = new[]
        {
            "https://sensitive-vendor.example.com/vault/order1.pdf",
            "http://insecure-vendor.org/docs/judgment.pdf",
            "s3://internal-bucket/raw-orders/secret.pdf",
            "//cdn.bpr-vendor.in/files/confidential.pdf"
        };

        foreach (var url in sensitiveUrls)
        {
            var order = new LitigationCaseOrder
            {
                LitigationCaseId = c.LitigationCaseId,
                OrderDate = "2025-02-01",
                OrderType = "Order",
                PdfUrl = url,
                CreatedUtc = DateTime.UtcNow
            };
            db.LitigationCaseOrders.Add(order);
            await db.SaveChangesAsync();

            db.LitigationOrderDocuments.Add(new LitigationOrderDocument
            {
                LitigationCaseOrderId = order.LitigationCaseOrderId,
                Status = LitigationOrderDocumentStatus.Downloaded,
                RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
                TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
                StoragePath = "C:\\fake\\doc.pdf"
            });
        }
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);
        Assert.NotNull(report);

        var pdfBytes = LitigationReportArtifacts.RenderPdf(report);
        var csvBytes = LitigationReportArtifacts.RenderCsv(report);

        using var pdfDoc = PdfDocument.Open(new MemoryStream(pdfBytes));
        var pdfText = string.Concat(pdfDoc.GetPages().Select(p => p.Text));
        var csvText = Encoding.UTF8.GetString(csvBytes);

        string[] prohibitedSubstrings =
        [
            "https://",
            "http://",
            "s3://",
            "sensitive-vendor.example.com",
            "insecure-vendor.org",
            "internal-bucket",
            "cdn.bpr-vendor.in",
            "order1.pdf",
            "judgment.pdf",
            "secret.pdf",
            "confidential.pdf"
        ];

        foreach (var sub in prohibitedSubstrings)
        {
            Assert.DoesNotContain(sub, pdfText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sub, csvText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 3. Extraction-Status Fidelity & Local Retention Contract ────────────

    [Fact]
    public async Task AssembleAsync_ExtractionFidelity_And_LocalRetentionContract_Preserved()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "RT" + Guid.NewGuid().ToString("N")[..6], ClientName = "Retention Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Retention Co", RequestNumber = $"REQ-RT-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Retention Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-rt-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-rt-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var c = new LitigationCase
        {
            RequestId = request.RequestId,
            CaseNumber = "CASE-RT-001",
            Court = "High Court",
            CourtCategory = "high_court",
            CaseStatus = "Disposed",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = c.LitigationCaseId,
            LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow
        });

        // Order 1: Downloaded but past retention date -> Remains Downloaded ("Available via portal")
        var o1 = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2025-01-01", OrderType = "Order 1", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(o1);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = o1.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(-10), // expired vendor window
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted
        });

        // Order 2: Not downloaded and past retention date -> Expired ("Vendor PDF expired")
        var o2 = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2024-12-01", OrderType = "Order 2", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(o2);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = o2.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Pending,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(-5), // expired vendor window
            TextExtractionStatus = null
        });

        // Order 3: Downloaded with CorruptPdf extraction status
        var o3 = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2024-11-01", OrderType = "Order 3", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(o3);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = o3.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5),
            TextExtractionStatus = FilingDocumentProcessingStatus.CorruptPdf
        });

        // Order 4: Downloaded with PasswordProtected extraction status
        var o4 = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2024-10-01", OrderType = "Order 4", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(o4);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = o4.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5),
            TextExtractionStatus = FilingDocumentProcessingStatus.PasswordProtected
        });

        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);
        Assert.NotNull(report);

        var reportCase = report.Cases[0];
        Assert.Equal(4, reportCase.Orders.Count);

        // Order 1 (Downloaded file kept locally even after retention expiry)
        var dto1 = reportCase.Orders.First(o => o.OrderType == "Order 1");
        Assert.Equal(LitigationOrderAvailabilityBucket.Downloaded, dto1.AvailabilityBucket);
        Assert.Contains("Available via portal", dto1.AvailabilityDisclosure);
        Assert.Equal("TextExtracted", dto1.ExtractionLabel);

        // Order 2 (Undownloaded file past retention expiry)
        var dto2 = reportCase.Orders.First(o => o.OrderType == "Order 2");
        Assert.Equal(LitigationOrderAvailabilityBucket.Expired, dto2.AvailabilityBucket);
        Assert.Contains("Vendor PDF expired", dto2.AvailabilityDisclosure);
        Assert.Equal("NotAttempted", dto2.ExtractionLabel);

        // Order 3 & 4 (CorruptPdf and PasswordProtected extraction statuses)
        var dto3 = reportCase.Orders.First(o => o.OrderType == "Order 3");
        Assert.Equal("CorruptPdf", dto3.ExtractionLabel);

        var dto4 = reportCase.Orders.First(o => o.OrderType == "Order 4");
        Assert.Equal("PasswordProtected", dto4.ExtractionLabel);

        // Verify CSV export preserves the exact extraction labels and bucket counts
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report));
        Assert.Contains("TextExtracted", csv);
        Assert.Contains("CorruptPdf", csv);
        Assert.Contains("PasswordProtected", csv);
        Assert.Contains("NotAttempted", csv);
    }

    // ── 4. Chronological Order Sorting ──────────────────────────────────────

    [Fact]
    public async Task AssembleAsync_OrdersAreSortedChronologically_Descending()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "SR" + Guid.NewGuid().ToString("N")[..6], ClientName = "Sort Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Sort Co", RequestNumber = $"REQ-SR-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Sort Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-sr-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-sr-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var c = new LitigationCase
        {
            RequestId = request.RequestId,
            CaseNumber = "CASE-SR-001",
            Court = "Delhi HC",
            CourtCategory = "high_court",
            CaseStatus = "Pending",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = c.LitigationCaseId,
            LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow
        });

        // Three orders with mixed dates: August 2024, Jan 2025, Dec 2024
        db.LitigationCaseOrders.AddRange(
            new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "15-08-2024", OrderType = "Order A", CreatedUtc = DateTime.UtcNow },
            new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "01-01-2025", OrderType = "Order B", CreatedUtc = DateTime.UtcNow },
            new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "31-12-2024", OrderType = "Order C", CreatedUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);
        Assert.NotNull(report);

        var orders = report.Cases[0].Orders;
        Assert.Equal("01-01-2025", orders[0].OrderDate); // Most recent
        Assert.Equal("31-12-2024", orders[1].OrderDate);
        Assert.Equal("15-08-2024", orders[2].OrderDate); // Oldest

        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report));
        Assert.Contains("01-01-2025; 31-12-2024; 15-08-2024", csv);
        Assert.Contains("Order B; Order C; Order A", csv);
    }

    // ── 5. Zero-Case Snapshot vs Missing Snapshot ───────────────────────────

    [Fact]
    public async Task AssembleAsync_ZeroCaseSnapshot_ReturnsValidCleanReport_And_ControllerReturns200()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "ZC" + Guid.NewGuid().ToString("N")[..6], ClientName = "Clean Due Diligence Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Clean Due Diligence Co", RequestNumber = $"REQ-ZC-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Clean Due Diligence Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-clean-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-clean-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            CasesPersistedCount = 0
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);

        // Assembler returns a clean report representing completed due diligence with 0 cases
        Assert.NotNull(report);
        Assert.Empty(report.Cases);
        Assert.Equal(0, report.CourtSummaryGrid.TotalCases);
        Assert.Equal(0, report.CourtSummaryGrid.TotalOrders);

        // PDF renders clean cover with zero-case note
        var pdfBytes = LitigationReportArtifacts.RenderPdf(report);
        Assert.True(pdfBytes.Length > 500);

        using var pdfDoc = PdfDocument.Open(new MemoryStream(pdfBytes));
        var pdfText = string.Concat(pdfDoc.GetPages().Select(p => p.Text));
        Assert.Contains("No court proceedings found", pdfText);

        // Controller returns 200 OK for clean due diligence
        var controller = CreateLitigationController(db);
        var pdfResult = await controller.DownloadPdfReport(request.RequestId, assembler, CancellationToken.None);
        var csvResult = await controller.DownloadCsvReport(request.RequestId, assembler, CancellationToken.None);

        var filePdf = Assert.IsType<FileContentResult>(pdfResult);
        Assert.Equal("application/pdf", filePdf.ContentType);

        var fileCsv = Assert.IsType<FileContentResult>(csvResult);
        Assert.Equal("text/csv; charset=utf-8", fileCsv.ContentType);
    }

    [Fact]
    public async Task AssembleAsync_MissingCompletedSnapshot_ReturnsNull_And_ControllerReturns404()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "MS" + Guid.NewGuid().ToString("N")[..6], ClientName = "Missing Snapshot Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Missing Snapshot Co", RequestNumber = $"REQ-MS-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            Status = LitigationSearchJobStatus.Failed,
            FailureReason = "API timeout",
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var report = await assembler.AssembleAsync(request.RequestId);
        Assert.Null(report);

        var controller = CreateLitigationController(db);
        var pdfResult = await controller.DownloadPdfReport(request.RequestId, assembler, CancellationToken.None);
        var csvResult = await controller.DownloadCsvReport(request.RequestId, assembler, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(pdfResult);
        Assert.IsType<NotFoundObjectResult>(csvResult);
    }

    // ── 6. Controller Header & Content Disposition Tests ────────────────────

    [Fact]
    public async Task Controller_DownloadEndpoints_SetRequiredSecurityHeadersAndFilenames()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "HD" + Guid.NewGuid().ToString("N")[..6], ClientName = "Header Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Header Test Co", RequestNumber = $"REQ-HD-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[{\"Value\":\"Header Test Co\",\"Source\":0}]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash-hd-001",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-hd-001",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var assembler = new LitigationReportAssembler(db);
        var controller = CreateLitigationController(db);

        var pdfResult = await controller.DownloadPdfReport(request.RequestId, assembler, CancellationToken.None);
        Assert.IsType<FileContentResult>(pdfResult);

        var response = controller.Response;
        Assert.Equal("no-referrer", response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store, private", response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("LitigationReport_HEADER_TEST_CO_", response.Headers.ContentDisposition.ToString());
        Assert.Contains(".pdf", response.Headers.ContentDisposition.ToString());
    }
}
