using System.Diagnostics;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.ObjectPool;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ComplianceTabRenderingTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var repoRoot = FindRepoRoot();
        var webAppDir = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis");
        var webRoot = Path.Combine(webAppDir, "wwwroot");

        var env = new TestWebHostEnvironment
        {
            ApplicationName = "MCAROC_Analysis",
            ContentRootPath = webAppDir,
            WebRootPath = webRoot,
            ContentRootFileProvider = new PhysicalFileProvider(webAppDir),
            WebRootFileProvider = new PhysicalFileProvider(webRoot)
        };
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        var diag = new DiagnosticListener("Microsoft.AspNetCore");
        services.AddSingleton<DiagnosticSource>(diag);
        services.AddSingleton<DiagnosticListener>(diag);
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        services.AddLogging();
        services.AddControllersWithViews();

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderComplianceTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_ComplianceTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_ComplianceTab", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<RequestDetailsViewModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private static RequestDetailsViewModel CreateViewModel() => new()
    {
        Request = new McaRequest { CompanyName = "Test Co", RequestNumber = "REQ-1" },
        Documents = []
    };

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task All_null_cibil_amounts_never_show_zero()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "PUNJAB NATIONAL BANK",
                DefaulterType = "Suit Filed",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.DoesNotContain("₹0", html);
        Assert.DoesNotContain("₹0.00", html);
        Assert.Contains("Not reported", html);
        Assert.Contains("unstated amounts across institutions", html);
        Assert.Contains("Not reported / incomplete", html);
    }

    [Fact]
    public async Task Mixed_known_missing_cibil_amounts_show_partial_coverage_warning()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = 42.50m,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // The "*" sits immediately outside the _Amount partial's <span> (#123, C5b).
        Assert.Contains("₹42.50 Cr</span>*", html);
        Assert.Contains("incomplete coverage (*unstated filings)", html);
        Assert.Contains("*coverage incomplete (unstated amounts)", html);
    }

    [Fact]
    public async Task Partial_or_all_null_epfo_contributions_never_show_fabricated_aggregate()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Regional Office",
                WageMonth = "2023-01",
                EmployeeCount = 100,
                ContributionAmountCrore = 1.25m
            },
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Regional Office",
                WageMonth = "2023-02",
                EmployeeCount = 105,
                ContributionAmountCrore = null
            },
            new EpfoContribution
            {
                EstablishmentId = "EST002",
                EstablishmentName = "Coastal Head Office",
                WageMonth = "2023-01",
                EmployeeCount = 50,
                ContributionAmountCrore = null
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // The legitimate January contribution (1.25) is shown exactly once — a fabricated aggregate
        // for EST001 (which has an unstated February amount) would show it a second time.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(html, System.Text.RegularExpressions.Regex.Escape("₹1.25 Cr")).Count;
        Assert.Equal(1, occurrences);
        Assert.DoesNotContain("₹0", html);
        Assert.Contains("Not reported", html);
        Assert.Contains("incomplete coverage", html);
    }

    [Fact]
    public async Task Individual_missing_epfo_contribution_values_render_as_dash()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Unit 1",
                WageMonth = "2023-03",
                EmployeeCount = 80,
                ContributionAmountCrore = null,
                PaymentStatus = "Paid"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // Individual missing contribution amount renders as em-dash "—", via the _Amount partial's own
        // null fallback (#123, C5b) — now inside a <span class="mca-amount">, not bare <td> text.
        Assert.True(
            html.Contains("<td class=\"text-end\"><span class=\"mca-amount\">—</span></td>") ||
            html.Contains("<td class=\"text-end\"><span class=\"mca-amount\">&#x2014;</span></td>"),
            "Expected individual missing contribution amount to render as em-dash inside the _Amount partial's <td class=\"text-end\">");
    }

    [Fact]
    public async Task Mixed_known_missing_across_different_banks_shows_partial_coverage_warning()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = 42.50m,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "PUNJAB NATIONAL BANK",
                DefaulterType = "Suit Filed",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("₹42.50 Cr</span>*", html);
        Assert.Contains("incomplete coverage (*unstated filings)", html);
        Assert.Contains("*coverage incomplete (unstated amounts)", html);
    }

    // ── A5 / #36 — EPFO establishment metadata + TRRN ──

    [Fact]
    public async Task Epfo_establishment_metadata_renders_even_with_no_monthly_annexure()
    {
        var vm = CreateViewModel();
        vm.EpfoEstablishments =
        [
            new EpfoEstablishment
            {
                EstablishmentId = "ORBBS0006003000",
                Name = "COASTAL PROJECTS LTD.",
                City = "BHUBANESWAR",
                DateOfSetup = new DateOnly(1995, 5, 5),
                PrincipalBusinessActivities = "BUILDING AND CONSTRUCTION INDUSTRY",
                ExemptionStatus = "PF: UNEXEMPTED",
                WorkingStatus = "LIVE ESTABLISHMENT",
            }
        ];
        // No EpfoContributions — the "EPFO Establishments" summary sheet was present, the annexure was not.

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("Establishment Profiles", html);
        Assert.Contains("BHUBANESWAR", html);
        Assert.Contains("BUILDING AND CONSTRUCTION INDUSTRY", html);
        Assert.Contains("ORBBS0006003000", html);
        Assert.Contains("No monthly contribution history for this establishment in this upload.", html);
    }

    [Fact]
    public async Task Epfo_contribution_history_shows_the_trrn_column()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "ORBBS0006003000",
                EstablishmentName = "COASTAL PROJECTS LTD.",
                WageMonth = "May, 2026",
                Trrn = "2606360162374",
                EmployeeCount = 5,
                ContributionAmountCrore = 0.12m,
                PaymentStatus = "Paid on Time"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("<th>TRRN</th>", html);
        Assert.Contains("2606360162374", html);
    }

    // ── A6 / #37 — column-drop fixes ──

    [Fact]
    public async Task Gst_registration_shows_jurisdiction_and_legal_name()
    {
        var vm = CreateViewModel();
        vm.GstRegistrations =
        [
            new GstRegistration
            {
                Gstin = "14AABCC1907E1ZC",
                State = "Manipur",
                Status = "Active",
                CentreJurisdiction = "IMPHAL II RANGE",
                StateJurisdiction = "Work Contracts",
                LegalNameOfBusiness = "COASTAL PROJECTS LIMITED"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("IMPHAL II RANGE", html);
        Assert.Contains("Work Contracts", html);
        Assert.Contains("COASTAL PROJECTS LIMITED", html);
    }

    [Fact]
    public async Task Auditor_observation_shows_firm_identity_when_parsed()
    {
        var vm = CreateViewModel();
        vm.AuditorObservations =
        [
            new AuditorObservation
            {
                FinancialYear = 2016,
                Basis = FinancialBasis.Standalone,
                AuditorName = "MANAS KUMAR MANIA",
                MembershipNumber = "300113",
                FirmName = "U K MAHAPATRA & CO",
                FirmRegistrationNumber = "320039E",
                ObservationText = "- MANAS KUMAR MANIA (Membership Number: 300113) of U K MAHAPATRA & CO (Registration Number: 320039E)."
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("MANAS KUMAR MANIA", html);
        Assert.Contains("U K MAHAPATRA &amp; CO", html);
        Assert.Contains("320039E", html);
        Assert.Contains("300113", html);
    }

    // ── A9 / #51 — Credit Ratings + Unaccepted Ratings ──

    [Fact]
    public async Task Credit_ratings_section_separates_accepted_from_unaccepted()
    {
        var vm = CreateViewModel();
        vm.CreditRatings =
        [
            new CreditRating
            {
                Agency = "CRISIL", RatingDate = new DateOnly(2020, 6, 15), Instrument = "Long Term Bank Facilities",
                Amount = 150.0m, Currency = "INR", Rating = "CRISIL A", Action = "Reaffirmed", Outlook = "Stable",
                IsAccepted = true
            },
            new CreditRating
            {
                Agency = "CARE", Instrument = "Term Loan", Amount = 50.0m, Currency = "INR", Rating = "CARE BB+",
                RatingDate = new DateOnly(2019, 3, 10), IsAccepted = false
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("CRISIL", html);
        Assert.Contains("Reaffirmed", html);
        Assert.Contains("Unaccepted Ratings", html);
        Assert.Contains("CARE", html);
        Assert.Contains("did not accept or participate", html);
    }

    // ── D7 / #62 — EPFO Key Indicators ──

    [Fact]
    public async Task Epfo_key_indicators_render_in_epfo_subtab_excluding_h6()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 10, ContributionAmountCrore = 0.5m }
        ];

        var metrics = new List<MetricResult>
        {
            MetricResult.Ok("PF remittance on-time rate", 95.0m, MetricUnit.Percent, "19 on-time of 20 assessed", "EpfoContribution.PaymentDate"),
            MetricResult.Ok("PF late-remittance count", 1m, MetricUnit.Count, "1 late of 20 assessed", "EpfoContribution.PaymentDate"),
            MetricResult.Ok("Latest-month PF contribution", 0.5m, MetricUnit.Crore, "May 2026", "EpfoContribution.ContributionAmountCrore"),
            MetricResult.Ok("Employee count (EPFO) + trend", 10m, MetricUnit.Count, "May 2026 (+2 vs May 2025: 8)", "EpfoContribution.EmployeeCount"),
            MetricResult.Ok("PF contribution per recorded EPFO employee (latest month)", 5000m, MetricUnit.Rupees, "May 2026", "EpfoContribution.ContributionAmountCrore"),
            MetricResult.Ok("Revenue per EPFO employee", 25.0m, MetricUnit.Crore, "FY2026 revenue vs May 2026 headcount", "FinancialYearData.Revenue"),
            MetricResult.Ok("Establishment count + locations + flags", 1m, MetricUnit.Count, "1 live of 1 establishment across 1 location", "EpfoEstablishment.WorkingStatus")
        };

        vm.KeyMetrics = [new MetricGroup("EPFO / labour", metrics)];

        var html = await RenderComplianceTabAsync(vm);

        // Subtab panel #sec-compliance-epfo should contain H1-H5 and H7
        Assert.Contains("PF remittance on-time rate", html);
        Assert.Contains("PF late-remittance count", html);
        Assert.Contains("Latest-month PF contribution", html);
        Assert.Contains("Employee count (EPFO)", html);
        Assert.Contains("PF contribution per recorded EPFO employee (latest month)", html);
        Assert.Contains("Establishment count", html);

        // H6 (Revenue per EPFO employee) is surfaced on Financials tab, NOT Compliance tab
        Assert.DoesNotContain("Revenue per EPFO employee", html);
    }

    [Fact]
    public async Task Epfo_key_indicators_render_when_epfo_records_are_empty()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions = [];
        vm.EpfoEstablishments = [];

        var metrics = new List<MetricResult>
        {
            MetricResult.Insufficient("PF remittance on-time rate", MetricUnit.Percent, "No EPFO contribution records on file", "EpfoContribution.PaymentDate"),
            MetricResult.Insufficient("PF late-remittance count", MetricUnit.Count, "No EPFO contribution records on file", "EpfoContribution.PaymentDate"),
            MetricResult.Insufficient("Latest-month PF contribution", MetricUnit.Crore, "No EPFO contribution records on file", "EpfoContribution.ContributionAmountCrore"),
            MetricResult.Insufficient("Employee count (EPFO) + trend", MetricUnit.Count, "No EPFO contribution records on file", "EpfoContribution.EmployeeCount"),
            MetricResult.Insufficient("PF contribution per recorded EPFO employee (latest month)", MetricUnit.Rupees, "No EPFO contribution records on file", "EpfoContribution.ContributionAmountCrore"),
            MetricResult.Insufficient("Establishment count + locations + flags", MetricUnit.Count, "No EPFO establishment records on file", "EpfoEstablishment.WorkingStatus")
        };

        vm.KeyMetrics = [new MetricGroup("EPFO / labour", metrics)];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("No EPFO data was extracted.", html);
        Assert.Contains("Key Indicators", html);
        Assert.Contains("No EPFO contribution records on file", html);
        Assert.Contains("No EPFO establishment records on file", html);
    }

    [Fact]
    public async Task Credit_ratings_key_indicators_render_with_text_metrics()
    {
        var vm = CreateViewModel();
        vm.CreditRatings =
        [
            new CreditRating
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL AA+",
                RatingDate = new DateOnly(2026, 9, 10),
                IsAccepted = true
            }
        ];

        var metrics = new List<MetricResult>
        {
            MetricResult.Ok("Latest rating (Term Loan - CRISIL)", "CRISIL AA+", "as of 10 Sep 2026",
                "CreditRating.Rating", "CreditRating.Instrument", "CreditRating.Agency")
        };

        vm.KeyMetrics = [new MetricGroup("Credit ratings", metrics)];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("sec-compliance-credit-ratings", html);
        Assert.Contains("Latest rating (Term Loan - CRISIL)", html);
        Assert.Contains("CRISIL AA&#x2B;", html);
        Assert.Contains("<strong>CRISIL AA&#x2B;</strong>", html); // Assert it is rendered as strong (HasValue is true)
        Assert.Contains("CRISIL AA+", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Credit_ratings_key_indicators_render_when_ratings_are_empty()
    {
        var vm = CreateViewModel();
        vm.CreditRatings = [];

        var metrics = new List<MetricResult>
        {
            MetricResult.Insufficient("Latest rating per instrument", MetricUnit.Text,
                "No credit rating records on file", "CreditRating.Rating", "CreditRating.Instrument", "CreditRating.RatingDate")
        };

        vm.KeyMetrics = [new MetricGroup("Credit ratings", metrics)];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("sec-compliance-credit-ratings", html);
        Assert.Contains("Latest rating per instrument", html);
        Assert.Contains("No credit rating records on file", html);
    }

    // ── G24 / #107 — GST Registrations table columns ──

    [Fact]
    public async Task Gst_registrations_table_renders_trade_name_taxpayer_type_nature_of_business_and_flags()
    {
        var vm = CreateViewModel();
        vm.GstRegistrations =
        [
            new GstRegistration
            {
                Gstin = "29AABCC1907E1ZK",
                LegalNameOfBusiness = "COASTAL INFRASTRUCTURE LTD",
                TradeName = "COASTAL BUILDERS",
                TaxpayerType = "Regular",
                State = "Karnataka",
                CentreJurisdiction = "RANGE-I",
                StateJurisdiction = "WARD-10",
                Status = "Active",
                RegistrationDate = new DateOnly(2018, 7, 1),
                CancellationDate = null,
                NatureOfBusinessActivities = "Works Contract / Infrastructure Development",
                Flags = "High Risk Return Delayed"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // Header assertions
        Assert.Contains("Trade Name", html);
        Assert.Contains("Taxpayer Type", html);
        Assert.Contains("Nature of Business", html);
        Assert.Contains("Flags", html);

        // Data row assertions
        Assert.Contains("COASTAL BUILDERS", html);
        Assert.Contains("Regular", html);
        Assert.Contains("Works Contract / Infrastructure Development", html);
        Assert.Contains("High Risk Return Delayed", html);
    }

    [Fact]
    public async Task Gst_registrations_table_handles_null_and_dash_flags_cleanly()
    {
        var vm = CreateViewModel();
        vm.GstRegistrations =
        [
            new GstRegistration
            {
                Gstin = "29AABCC1907E1ZK",
                TradeName = null,
                TaxpayerType = null,
                NatureOfBusinessActivities = null,
                Flags = "-"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("29AABCC1907E1ZK", html);
        Assert.DoesNotContain("<span class=\"badge bg-light text-dark\">-</span>", html);
    }

    // ── #113 (C3) — workbook-lineage provenance column ──

    [Fact]
    public async Task Gst_registration_row_shows_its_source_sheet_and_row()
    {
        var vm = CreateViewModel();
        vm.GstRegistrations =
        [
            new GstRegistration { Gstin = "29AABCC1907E1ZK", SourceSheetName = "GST Registration Details", SourceRowNumber = 2 }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("Source: GST Registration Details, row 2", html);
    }

    // ── G25 / #107 — EPFO Contribution table columns ──

    [Fact]
    public async Task Epfo_contribution_history_table_renders_payment_due_date_and_payment_date()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "MH/BAN/0012345/000",
                EstablishmentName = "Coastal Regional Office",
                WageMonth = "2023-03",
                Trrn = "1012304056789",
                EmployeeCount = 150,
                ContributionAmountCrore = 0.45m,
                PaymentDueDate = new DateOnly(2023, 4, 15),
                PaymentDate = new DateOnly(2023, 4, 20),
                PaymentStatus = "Late"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // Header assertions
        Assert.Contains("Due Date", html);
        Assert.Contains("Payment Date", html);

        // Data row assertions
        Assert.Contains("15 Apr 2023", html);
        Assert.Contains("20 Apr 2023", html);
        Assert.Contains("Late", html);
    }
}

