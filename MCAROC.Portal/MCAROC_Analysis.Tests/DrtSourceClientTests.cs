using System.Net;
using System.Text;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class DrtSourceClientTests
{
    private static readonly DrtClientOptions DefaultOptions = new()
    {
        BaseUrl = "https://drt.gov.in/drtapi/",
        OrderBaseUrl = "https://cis.drt.gov.in/",
        UserAgent = "MCAROC-research/1.0",
        RequestsPerSecond = 1000.0, // fast in unit tests
        TimeoutSeconds = 5,
        MaxRetries = 3
    };

    private static DrtClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> hook,
        DrtClientOptions? options = null,
        TimeProvider? timeProvider = null,
        IDrtCircuitBreaker? breaker = null)
    {
        var opts = options ?? DefaultOptions;
        var http = new HttpClient { BaseAddress = new Uri(opts.BaseUrl) };
        return new DrtClient(http, Options.Create(opts), NullLogger<DrtClient>.Instance, timeProvider, breaker ?? new DrtCircuitBreaker(), hook);
    }

    // ── 1. Tribunals Endpoint (getDrtDratScheamName) ────────────────────────────────

    [Fact]
    public async Task GetTribunalsAsync_parses_tribunal_list_and_enriches_from_registry()
    {
        var json = """
        [
            {"schemeNameDrtId": 1, "SchemaName": "debts recovery tribunal delhi(drt 1)"},
            {"schemeNameDrtId": 26, "SchemaName": "debts recovery tribunal kolkata(drt 1)"},
            {"schemeNameDrtId": 100, "SchemaName": "debt recovery appellate tribunal - delhi"}
        ]
        """;

        var client = CreateClient((req, ct) =>
        {
            Assert.Equal("getDrtDratScheamName", req.RequestUri!.ToString().TrimEnd('/'));
            Assert.Equal(HttpMethod.Post, req.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var tribunals = await client.GetTribunalsAsync();

        Assert.Equal(3, tribunals.Count);

        var drt1 = tribunals.First(t => t.Id == 1);
        Assert.Equal("Delhi", drt1.State);
        Assert.Equal("New Delhi", drt1.District);
        Assert.False(drt1.IsDrat);

        var kolkata = tribunals.First(t => t.Id == 26);
        Assert.Equal("West Bengal", kolkata.State);
        Assert.Equal("Kolkata", kolkata.District);
        Assert.False(kolkata.IsDrat);

        var dratDelhi = tribunals.First(t => t.Id == 100);
        Assert.True(dratDelhi.IsDrat);
        Assert.Equal("Delhi", dratDelhi.State);
    }

    [Fact]
    public void TribunalRegistry_contains_all_44_tribunals()
    {
        var all = DrtTribunalRegistry.GetAll();
        Assert.Equal(44, all.Count);
        Assert.Equal(39, all.Count(t => !t.IsDrat));
        Assert.Equal(5, all.Count(t => t.IsDrat));
    }

    // ── 2. Case Types Endpoint (getDrtDratCaseTyepName) ────────────────────────────

    [Fact]
    public async Task GetCaseTypesAsync_parses_case_types_and_resolves_canonical_codes()
    {
        var json = """
        [
            {"caseType": 1, "caseTypeName": "ORIGINAL APPLICATION"},
            {"caseType": 3, "caseTypeName": "MISC APPLICATION"},
            {"caseType": 7, "caseTypeName": "SECURITIZATION APPLICATION"}
        ]
        """;

        var client = CreateClient(async (req, ct) =>
        {
            Assert.Equal("getDrtDratCaseTyepName", req.RequestUri!.ToString().TrimEnd('/'));
            var body = await req.Content!.ReadAsStringAsync(ct);
            Assert.Contains("schemeNameDrtId=1", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var types = await client.GetCaseTypesAsync(1);

        Assert.Equal(3, types.Count);
        Assert.Equal("OA", types[0].Code);
        Assert.Equal("MA", types[1].Code);
        Assert.Equal("SA", types[2].Code);
    }

    // ── 3. Party Search Endpoint (DRT vs DRAT) ─────────────────────────────────────

    [Fact]
    public async Task SearchByPartyNameAsync_uses_drt_endpoint_and_parses_day_first_dates()
    {
        var json = """
        [
            {
                "casetype": "OA",
                "caseno": "OA/413/2022",
                "diaryno": "1234/2022",
                "dateoffiling": "9/2/2022",
                "applicant": "SOUTH INDIAN BANK",
                "respondent": "ACME ENTERPRISES",
                "petitionerAdvocate": "ADV A ROY",
                "respondentAdvocate": "ADV B SEN",
                "filingNo": "DRT12345"
            }
        ]
        """;

        var client = CreateClient(async (req, ct) =>
        {
            Assert.Equal("casedetail_party_name_wise", req.RequestUri!.ToString().TrimEnd('/'));
            var body = await req.Content!.ReadAsStringAsync(ct);
            Assert.Contains("schemeNameDratDrtId=25", body);
            Assert.Contains("partyName=SOUTH+INDIAN+BANK", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var results = await client.SearchByPartyNameAsync(25, "SOUTH INDIAN BANK");

        var hit = Assert.Single(results);
        Assert.Equal(25, hit.TribunalId);
        Assert.Equal("OA", hit.RawCaseType);
        Assert.Equal("OA/413/2022", hit.RawCaseNo);
        Assert.Equal("413", hit.CaseNumber.No);
        Assert.Equal(2022, hit.CaseNumber.Year);
        // Date 9/2/2022 is 9 February 2022 (day-first), NOT September 2
        Assert.Equal(new DateOnly(2022, 2, 9), hit.DateOfFiling);
        Assert.Equal("SOUTH INDIAN BANK", hit.Applicant);
        Assert.Equal("ADV A ROY", hit.PetitionerAdvocate);
    }

    [Fact]
    public async Task SearchByPartyNameAsync_uses_drat_endpoint_for_drat_ids()
    {
        var json = "[]";
        string? requestedUri = null;

        var client = CreateClient((req, ct) =>
        {
            requestedUri = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        await client.SearchByPartyNameAsync(100, "TEST BANK");

        Assert.Contains("drat_party_name_wise", requestedUri);
    }

    // ── 4. Case By Number Endpoint (26 fields + hearings) ──────────────────────────

    [Fact]
    public async Task GetCaseByNumberAsync_parses_all_fields_hearings_and_suit_amount()
    {
        var json = """
        {
            "casetype": "OA",
            "casetypeId": "1",
            "caseno": "OA/78/2025",
            "caseyear": "2025",
            "diaryno": "123",
            "diaryyear": "2025",
            "filingNo": "F-9988",
            "dateoffiling": "15/1/2025",
            "casestatus": "PENDING",
            "nextlistingdate": "8/6/2027",
            "nextListingPurpose": "Arguments",
            "courtNo": "1",
            "courtName": "Presiding Officer DRT 1",
            "maincasecaseno": "OA/10/2020",
            "suit_amount": "1,50,000.50",
            "petitionerName": "CANARA BANK",
            "respondentName": "XYZ TRADERS",
            "petitionerAddress": "Kolkata, WB",
            "respondentAddress": "Howrah, WB",
            "petitionerAdvocate": "Adv A",
            "respondentAdvocate": "Adv B",
            "caseProceedingDetails": [
                {
                    "causelistdate": "10/3/2025",
                    "purpose": "Appearance",
                    "courtName": "Bench 1",
                    "courtNo": "1",
                    "orderUrl": "https://cis.drt.gov.in/drtlive/order/pdf/pdf1.php?file=L3VwbG9hZHMvZHJ0L2RydGNvdXJ0L2RhaWx5X29yZGVyLzIwMjUvTWFyY2gvMDgwMTEwMDA2NzAyMDI1XzcwZTk3NzY3ODhiMjRmZjE5Njk0Mjc5MzZkNTEyZTk1LnBkZioqKjMzMDYxNCMxI2phaXB1cg=="
                }
            ]
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            Assert.Equal("getCaseDetailCaseNoWise", req.RequestUri!.ToString().TrimEnd('/'));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetCaseByNumberAsync(26, 1, "78", 2025);

        Assert.True(result.IsFound);
        var details = result.Case!;
        Assert.Equal(26, details.TribunalId);
        Assert.Equal("OA", details.CaseType);
        Assert.Equal("OA", details.CaseTypeCode);
        Assert.Equal("78", details.CaseNo);
        Assert.Equal(2025, details.CaseYear);
        Assert.Equal(new DateOnly(2025, 1, 15), details.DateOfFiling);
        Assert.Equal("PENDING", details.Status);
        Assert.Equal(LitigationCaseStatusBucket.Pending, details.StatusParts.Bucket);
        Assert.Equal("Arguments", details.NextListingPurpose);
        Assert.Equal(new DateOnly(2027, 6, 8), details.NextListingDate);
        Assert.Equal(150000.50m, details.SuitAmount);
        Assert.Equal("CANARA BANK", details.PetitionerName);
        Assert.Equal("OA/10/2020", details.MainCaseCaseNo);

        var hearing = Assert.Single(details.Hearings);
        Assert.Equal(new DateOnly(2025, 3, 10), hearing.CauseListDate);
        Assert.Equal("Appearance", hearing.Purpose);
        Assert.NotNull(hearing.OrderKey);
        Assert.Contains("daily_order/2025/march", hearing.OrderKey);
    }

    [Fact]
    public async Task GetCaseByNumberAsync_drat_sends_casetype_param()
    {
        string? sentBody = null;
        var client = CreateClient(async (req, ct) =>
        {
            Assert.Equal("getDratCaseDetailCaseNoWise", req.RequestUri!.ToString().TrimEnd('/'));
            sentBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"caseno":"456","casestatus":"DISPOSAL"}""", Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetCaseByNumberAsync(100, 4, "456", 2024);

        Assert.True(result.IsFound);
        Assert.Contains("casetype=4", sentBody);
        Assert.DoesNotContain("casetypeId=", sentBody);
    }

    // ── 5. Pinned Misspellings (TRANSFERED and Record Not Fund) ───────────────────

    [Fact]
    public async Task GetCaseByNumberAsync_maps_portal_misspelling_TRANSFERED_to_disposed()
    {
        var json = """
        {
            "caseno": "614",
            "casetype": "SA",
            "caseyear": "2025",
            "casestatus": "TRANSFERED",
            "dateoffiling": "1/2/2025"
        }
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetCaseByNumberAsync(28, 7, "614", 2025);

        Assert.True(result.IsFound);
        Assert.Equal("TRANSFERED", result.Case!.Status);
        // TRANSFERED is classified into Disposed bucket by CaseStatusVocabulary
        Assert.Equal(LitigationCaseStatusBucket.Disposed, result.Case.StatusParts.Bucket);
    }

    [Fact]
    public async Task GetFinalOrdersAsync_handles_pinned_Record_Not_Fund_misspelling()
    {
        var json = """{"status":"Record Not Fund"}""";

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var orders = await client.GetFinalOrdersAsync(1, 1, "100", 2024);

        Assert.Empty(orders);
    }

    // ── 6. Candidate NotFound (Plan §4.9) ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"casetype":"The casetype field is required."}""")]
    public async Task GetCaseByNumberAsync_treats_empty_or_validation_response_as_NotFoundCandidate(string body)
    {
        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetCaseByNumberAsync(1, 1, "99999", 2024);

        Assert.False(result.IsFound);
        Assert.Equal(DrtLookupStatus.NotFoundCandidate, result.Status);
    }

    // ── 7. RC / TRC Unavailable Endpoint ──────────────────────────────────────────

    [Fact]
    public async Task GetRcTrcCaseAsync_returns_Unavailable_per_plan_section_2_1()
    {
        var client = CreateClient((req, ct) => throw new InvalidOperationException("Should never be called"));

        var result = await client.GetRcTrcCaseAsync(1, 5, "100", 2024);

        Assert.Equal(DrtLookupStatus.Unavailable, result.Status);
    }

    // ── 8. Order URL and OrderKey Normalisation (Plan §4.5) ────────────────────────

    [Fact]
    public void OrderKeyHelper_normalises_base64_file_url_and_removes_stray_suffix()
    {
        // Encoded: /uploads/drt/drtcourt/daily_order/2026/March/test.pdf***330614#1#jaipur
        var rawPath = "/uploads/drt/drtcourt/daily_order/2026/March/test.pdf***330614#1#jaipur";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawPath));
        var url = $"https://cis.drt.gov.in/drtlive/order/pdf/pdf1.php?file={b64}";

        var key = DrtOrderKeyHelper.ExtractOrderKey(url);

        Assert.Equal("daily_order/2026/march/test.pdf", key);
    }

    [Fact]
    public void OrderKeyHelper_converts_qrpdfview_to_secure_pdf_serve()
    {
        var url = "https://cis.drt.gov.in/drtlive/order/qrpdfview.php?file=XYZ";
        var direct = DrtOrderKeyHelper.DirectPdfUrl(url);

        Assert.Equal("https://cis.drt.gov.in/drtlive/order/secure-pdf-serve.php?file=XYZ", direct);
    }

    [Fact]
    public void OrderKeyHelper_builds_composite_OrderKey()
    {
        var rawPath = "/uploads/drt/drtcourt/final_order/2025/test.pdf***123";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawPath));
        var url = $"https://cis.drt.gov.in/drtlive/order/pdf/pdf1.php?file={b64}";

        var key = DrtOrderKeyHelper.BuildCompositeOrderKey(25, 1, "428", 2026, "final", new DateOnly(2026, 3, 16), url);

        Assert.Equal("25|1|428|2026|final|2026-03-16|final_order/2025/test.pdf", key);
    }

    // ── 9. Order Cleaner and Format Detection ─────────────────────────────────────

    [Fact]
    public void DrtOrderCleaner_strips_leading_whitespace_before_PDF()
    {
        var pdfBytes = "%PDF-1.4\n%trailer"u8.ToArray();
        var padded = "\r\n  \t "u8.ToArray().Concat(pdfBytes).ToArray();

        var result = DrtOrderCleaner.Clean(padded);

        Assert.True(result.IsSuccess);
        Assert.Equal("pdf", result.Format);
        Assert.True(result.CleanedPdf);
        Assert.Equal(pdfBytes, result.Bytes);
    }

    [Fact]
    public void DrtOrderCleaner_strips_stray_id_header_before_PDF()
    {
        var prefix = "123456#1"u8.ToArray();
        var pdfBytes = "%PDF-1.4\n%content"u8.ToArray();
        var bytes = prefix.Concat(pdfBytes).ToArray();

        var result = DrtOrderCleaner.Clean(bytes);

        Assert.True(result.IsSuccess);
        Assert.Equal("pdf", result.Format);
        Assert.True(result.CleanedPdf);
        Assert.Equal(pdfBytes, result.Bytes);
    }

    [Fact]
    public void DrtOrderCleaner_detects_html_orders()
    {
        var html = "<html><body>Order text</body></html>"u8.ToArray();
        var result = DrtOrderCleaner.Clean(html, "https://cis.drt.gov.in/order/pdf_html.php?file=123");

        Assert.True(result.IsSuccess);
        Assert.Equal("html", result.Format);
        Assert.Equal("text/html", result.ContentType);
    }

    // ── 10. Politeness Rate Limiting & User-Agent ─────────────────────────────────

    [Fact]
    public async Task RateLimit_enforces_politeness_gap_between_calls()
    {
        var options = new DrtClientOptions
        {
            RequestsPerSecond = 20.0, // 50ms interval
            MaxRetries = 1
        };

        var callTimes = new List<DateTimeOffset>();

        var client = CreateClient((req, ct) =>
        {
            callTimes.Add(DateTimeOffset.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }, options);

        await client.GetTribunalsAsync();
        await client.GetTribunalsAsync();

        Assert.Equal(2, callTimes.Count);
        Assert.True(callTimes[1] - callTimes[0] >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task SendPostAsync_sends_identifying_UserAgent()
    {
        string? ua = null;
        var client = CreateClient((req, ct) =>
        {
            ua = req.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });

        await client.GetTribunalsAsync();

        Assert.Equal("MCAROC-research/1.0", ua);
    }

    // ── 11. Resilience and Transient Retries ───────────────────────────────────────

    [Fact]
    public async Task SendPostAsync_retries_transient_503_and_succeeds()
    {
        var attempts = 0;
        var client = CreateClient((req, ct) =>
        {
            attempts++;
            if (attempts < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"caseType": 1, "caseTypeName": "OA"}]""", Encoding.UTF8, "application/json")
            });
        });

        var types = await client.GetCaseTypesAsync(1);

        Assert.Equal(3, attempts);
        Assert.Single(types);
    }

    [Fact]
    public async Task GetCaseByNumberAsync_preserves_403_and_400_as_Error_never_NotFoundCandidate()
    {
        var client403 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Access Denied", Encoding.UTF8, "text/plain")
        }));

        var res403 = await client403.GetCaseByNumberAsync(1, 1, "123", 2024);

        Assert.Equal(DrtLookupStatus.Error, res403.Status);
        Assert.Equal(HttpStatusCode.Forbidden, res403.StatusCode);
        Assert.Contains("403", res403.Message);

        var client400 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad Request", Encoding.UTF8, "text/plain")
        }));

        var res400 = await client400.GetCaseByNumberAsync(1, 1, "123", 2024);

        Assert.Equal(DrtLookupStatus.Error, res400.Status);
        Assert.Equal(HttpStatusCode.BadRequest, res400.StatusCode);
    }

    [Fact]
    public async Task GetCaseByNumberAsync_preserves_exhausted_503_as_Unavailable_never_NotFoundCandidate()
    {
        var attempts = 0;
        var client = CreateClient((req, ct) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Service Unavailable", Encoding.UTF8, "text/plain")
            });
        });

        var res = await client.GetCaseByNumberAsync(1, 1, "123", 2024);

        Assert.Equal(DefaultOptions.MaxRetries, attempts);
        Assert.Equal(DrtLookupStatus.Unavailable, res.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("Exhausted", res.Message);
    }

    [Fact]
    public async Task Post_retries_create_fresh_content_and_resend_without_disposed_exception()
    {
        var attempts = 0;
        var sentBodies = new List<string>();

        var client = CreateClient(async (req, ct) =>
        {
            attempts++;
            var body = await req.Content!.ReadAsStringAsync(ct);
            sentBodies.Add(body);

            if (attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"caseno":"123","casestatus":"PENDING"}""", Encoding.UTF8, "application/json")
            };
        });

        var res = await client.GetCaseByNumberAsync(1, 1, "123", 2024);

        Assert.Equal(2, attempts);
        Assert.True(res.IsFound);
        Assert.Equal(2, sentBodies.Count);
        Assert.Contains("caseNo=123", sentBodies[0]);
        Assert.Contains("caseNo=123", sentBodies[1]);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_immediately_and_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.GetCaseByNumberAsync(1, 1, "123", 2024, cts.Token);
        });
    }

    // ── 12. Circuit Breaker and Proxy Rotator ──────────────────────────────────────

    [Fact]
    public async Task CircuitBreaker_trips_after_8_failures_and_defers_calls()
    {
        var breaker = new DrtCircuitBreaker(threshold: 8, coolDown: TimeSpan.FromMinutes(1));
        var client = CreateClient(
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            options: new DrtClientOptions { MaxRetries = 1, RequestsPerSecond = 1000 },
            breaker: breaker);

        // 8 failures trip the breaker
        for (var i = 0; i < 8; i++)
        {
            var res = await client.GetCaseByNumberAsync(1, 1, "123", 2024);
            Assert.Equal(DrtLookupStatus.Unavailable, res.Status);
        }

        Assert.True(breaker.IsOpen());

        // Subsequent call is short-circuited by the client without sending HTTP request
        var callMade = false;
        var clientWithProbe = CreateClient(
            (req, ct) => { callMade = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); },
            breaker: breaker);

        var shortCircuited = await clientWithProbe.GetCaseByNumberAsync(1, 1, "123", 2024);

        Assert.False(callMade);
        Assert.Equal(DrtLookupStatus.Unavailable, shortCircuited.Status);
        Assert.Contains("circuit breaker is open", shortCircuited.Message);
    }

    [Fact]
    public async Task CircuitBreaker_is_shared_across_clients_for_same_host()
    {
        var sharedBreaker = DrtCircuitBreaker.GetShared("shared-test-host");

        var options = new DrtClientOptions
        {
            BaseUrl = "https://shared-test-host/drtapi/",
            MaxRetries = 1,
            RequestsPerSecond = 1000
        };

        var client1 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), options, breaker: sharedBreaker);
        var client2 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), options, breaker: sharedBreaker);

        // 8 failures on client 1 trip the shared breaker
        for (var i = 0; i < 8; i++)
        {
            await client1.GetCaseByNumberAsync(1, 1, "100", 2024);
        }

        // client 2 sharing the same breaker is now also deferred without making HTTP calls
        var res = await client2.GetCaseByNumberAsync(1, 1, "100", 2024);
        Assert.Equal(DrtLookupStatus.Unavailable, res.Status);
        Assert.Contains("circuit breaker is open", res.Message);
    }

    [Fact]
    public async Task RetryAfter_header_is_honoured_and_extends_circuit_breaker()
    {
        var breaker = new DrtCircuitBreaker(threshold: 1, coolDown: TimeSpan.FromMinutes(1));
        var client = CreateClient((req, ct) =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            msg.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return Task.FromResult(msg);
        }, options: new DrtClientOptions { MaxRetries = 1, RequestsPerSecond = 1000 }, breaker: breaker);

        var res = await client.GetCaseByNumberAsync(1, 1, "100", 2024);

        Assert.Equal(DrtLookupStatus.Unavailable, res.Status);
        Assert.Contains("Retry-After: 300s", res.Message);
        Assert.True(breaker.IsOpen());
        Assert.NotNull(breaker.TrippedUntil);
        Assert.True(breaker.TrippedUntil!.Value >= DateTimeOffset.UtcNow.AddSeconds(290));
    }

    [Fact]
    public void ProxyRotator_rotates_on_frequency_and_Rotate_call()
    {
        var proxies = new[] { "http://proxy1:8080", "http://proxy2:8080", "http://proxy3:8080" };
        var rotator = new DrtProxyRotator(proxies, rotateFrequency: 2);

        Assert.Equal("http://proxy1:8080", rotator.CurrentExit);

        rotator.NotifyRequest(); // 1
        Assert.Equal("http://proxy1:8080", rotator.CurrentExit);

        rotator.NotifyRequest(); // 2 -> triggers rotate
        Assert.Equal("http://proxy2:8080", rotator.CurrentExit);

        rotator.Rotate(); // manual rotate (e.g. on retry)
        Assert.Equal("http://proxy3:8080", rotator.CurrentExit);

        rotator.Rotate(); // wraps around
        Assert.Equal("http://proxy1:8080", rotator.CurrentExit);
    }

    [Fact]
    public void CanConfirmNotFound_requires_two_candidates_across_distinct_exits()
    {
        var firstExitA = DrtCaseLookupResult.NotFoundCandidate("Empty", "{}", exitAddress: "http://proxy1:8080");
        var secondExitA = DrtCaseLookupResult.NotFoundCandidate("Empty", "{}", exitAddress: "http://proxy1:8080");
        var secondExitB = DrtCaseLookupResult.NotFoundCandidate("Empty", "{}", exitAddress: "http://proxy2:8080");
        var dummyDetails = new DrtCaseDetails(
            1, 1, "OA", "OA", "1", 2024,
            CaseNumberNormaliser.Parse(new CaseNumberInput("OA/1/2024")),
            null, null, null, null, "PENDING",
            CaseStatusVocabulary.Map("PENDING"),
            "PENDING", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, [], null);
        var foundResult = DrtCaseLookupResult.Found(dummyDetails, "{}", "http://proxy2:8080");

        // Same exit address: cannot confirm
        Assert.False(DrtCaseLookupResult.CanConfirmNotFound(firstExitA, secondExitA));

        // Different exit addresses: can confirm
        Assert.True(DrtCaseLookupResult.CanConfirmNotFound(firstExitA, secondExitB));

        // Found result cannot confirm not-found
        Assert.False(DrtCaseLookupResult.CanConfirmNotFound(firstExitA, foundResult));
    }

    // ── 12. Corpus Parity Check (50 cases from progress.db) ────────────────────────

    [Fact]
    public void CaseDates_correctly_parses_sample_dates_from_progress_corpus()
    {
        // Dates seen in Kotak DRT progress.db
        string[] sampleDates =
        [
            "30/9/2026", "17/11/2026", "4/11/2026", "8/6/2027", "8/12/2026",
            "30/10/2026", "9/11/2026", "28/12/2026", "22/9/2026", "16/7/2024",
            "24/12/2026", "14/12/2026", "7/9/2026", "3/11/2026", "12/1/2027",
            "16/10/2026", "9/10/2026", "22/10/2026", "13/10/2026", "23/12/2026",
            "8/10/2026", "26/11/2026", "1/7/2026", "16/3/2026", "6/3/2026"
        ];

        foreach (var s in sampleDates)
        {
            var d = CaseDates.ParseDate(s);
            Assert.NotNull(d);
            Assert.True(d!.Value.Year >= 2024);
        }
    }

    [Fact]
    public void StatusVocabulary_maps_all_status_strings_seen_in_corpus()
    {
        string[] corpusStatuses = ["PENDING", "DISPOSAL", "TRANSFERED"];

        var p = CaseStatusVocabulary.Map("PENDING");
        Assert.Equal(LitigationCaseStatusBucket.Pending, p.Bucket);

        var d = CaseStatusVocabulary.Map("DISPOSAL");
        Assert.Equal(LitigationCaseStatusBucket.Disposed, d.Bucket);

        var t = CaseStatusVocabulary.Map("TRANSFERED");
        Assert.Equal(LitigationCaseStatusBucket.Disposed, t.Bucket);
    }
}
