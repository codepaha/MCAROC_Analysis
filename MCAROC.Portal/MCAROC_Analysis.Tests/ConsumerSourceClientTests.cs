using System.Net;
using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class ConsumerSourceClientTests
{
    private static readonly ConsumerClientOptions DefaultOptions = new()
    {
        BaseUrl = "https://e-jagriti.gov.in/",
        UserAgent = "MCAROC-research/1.0",
        RequestsPerSecond = 1000.0, // fast in unit tests
        TimeoutSeconds = 5,
        MaxRetries = 3
    };

    private static ConsumerClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> hook,
        ConsumerClientOptions? options = null,
        TimeProvider? timeProvider = null,
        IConsumerCircuitBreaker? breaker = null,
        IWebProxy? proxy = null)
    {
        var opts = options ?? DefaultOptions;
        var http = ConsumerClient.CreateHttpClient(opts, proxy);
        return new ConsumerClient(
            http,
            Options.Create(opts),
            NullLogger<ConsumerClient>.Instance,
            timeProvider,
            breaker ?? new ConsumerCircuitBreaker(),
            hook);
    }

    // ── 1. getCaseStatus Endpoint Contract Tests ─────────────────────────────────

    [Fact]
    public async Task GetCaseStatusAsync_parses_real_fixture_and_pins_contract_misspellings()
    {
        var json = """
        {
            "message": "success",
            "status": 200,
            "data": {
                "caseNumber": "DC/497/CC/599/2017",
                "fillingReferenceNumber": 100000651664,
                "caseFilingDate": "2017-09-27T18:30:00.000+00:00",
                "filedInComissionName": "Thane",
                "complainant": "Complainant ABC",
                "respondent": "Company XYZ Ltd",
                "caseStage": "ALLOWED",
                "dateOfNextearing": null,
                "complainantAdvocate": ["ADV X"],
                "respondentAdvocate": ["ADV Y"],
                "applicationsFiled": ["IA/1/2017"],
                "attachedLowerCourt": [],
                "caseHearingDetails": [
                    {
                        "courtRoomHearingId": "5278302",
                        "dateOfHearing": "2017-10-16",
                        "dateOfNextHearing": "2018-01-19",
                        "caseStage": "REGISTERED",
                        "proceedingText": "<p>Matter registered</p>",
                        "dailyOrderStatus": true,
                        "orderTypeId": 1
                    },
                    {
                        "courtRoomHearingId": "5278303",
                        "dateOfHearing": "2025-11-27",
                        "dateOfNextHearing": null,
                        "caseStage": "ALLOWED",
                        "proceedingText": "<p>Matter allowed</p>",
                        "dailyOrderStatus": true,
                        "orderTypeId": 1
                    }
                ]
            }
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("getCaseStatus", req.RequestUri!.ToString());
            Assert.Contains("caseNumber=DC%2F497%2FCC%2F599%2F2017", req.RequestUri!.ToString());
            Assert.Equal("MCAROC-research/1.0", req.Headers.UserAgent.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetCaseStatusAsync("DC/497/CC/599/2017");

        Assert.True(result.IsFound);
        var c = result.Case;
        Assert.NotNull(c);

        // Assert contract pinned misspellings
        Assert.Equal(100000651664L, c.FilingReferenceNumber); // fillingReferenceNumber
        Assert.Equal("Thane", c.FiledInCommissionName);       // filedInComissionName
        Assert.Null(c.DateOfNextHearing);                     // dateOfNextearing

        // Assert UTC timestamp converted to IST calendar date (2017-09-27T18:30:00Z -> 2017-09-28 IST)
        Assert.Equal(new DateOnly(2017, 9, 28), c.CaseFilingDate);

        // Assert parties and advocates
        Assert.Equal("Complainant ABC", c.Complainant);
        Assert.Equal("Company XYZ Ltd", c.Respondent);
        Assert.Equal("ADV X", c.ComplainantAdvocate);
        Assert.Equal("ADV Y", c.RespondentAdvocate);
        Assert.Equal("ALLOWED", c.CaseStage);
        Assert.True(c.IsTerminal);

        // Assert hearings
        Assert.Equal(2, c.Hearings.Count);
        Assert.Equal("5278302", c.Hearings[0].CourtRoomHearingId);
        Assert.True(c.Hearings[0].DailyOrderAvailable);
        Assert.Equal(new DateOnly(2017, 10, 16), c.Hearings[0].DateOfHearing);

        // Assert derived decision date for terminal case
        Assert.True(c.IsDecisionDateDerived);
        Assert.Equal(new DateOnly(2025, 11, 27), c.DerivedDecisionDate);

        // Assert derived State and District from registry
        Assert.Equal("MAHARASHTRA", c.StateName);
        Assert.Equal("Thane", c.DistrictName);
        Assert.Equal(12270497, c.CommissionId);
        Assert.Equal(3, c.CommissionTypeId);

        // Assert normalized case number
        Assert.Equal("CC/599/2017", c.NormalizedCaseNumber.Display);
        Assert.Equal("DC/497/CC/599/2017", c.NormalizedCaseNumber.PortalNumber);
    }

    [Fact]
    public async Task GetCaseStatusAsync_handles_three_case_number_shapes()
    {
        // 1. Full shape: DC/497/CC/599/2017
        var fullJson = """
        {"status": 200, "data": {"caseNumber": "DC/497/CC/599/2017", "caseStage": "PENDING"}}
        """;
        var client1 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fullJson, Encoding.UTF8, "application/json")
        }));
        var res1 = await client1.GetCaseStatusAsync("DC/497/CC/599/2017");
        Assert.True(res1.IsFound);
        Assert.Equal("CC/599/2017", res1.Case!.NormalizedCaseNumber.Display);
        Assert.Equal("DC/497/CC/599/2017", res1.Case.NormalizedCaseNumber.PortalNumber);

        // 2. National Commission shape: NC/RP/836/2005
        var nationalJson = """
        {"status": 200, "data": {"caseNumber": "NC/RP/836/2005", "caseStage": "PENDING"}}
        """;
        var client2 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(nationalJson, Encoding.UTF8, "application/json")
        }));
        var res2 = await client2.GetCaseStatusAsync("NC/RP/836/2005");
        Assert.True(res2.IsFound);
        Assert.Equal("RP/836/2005", res2.Case!.NormalizedCaseNumber.Display);
        Assert.Equal("NC/RP/836/2005", res2.Case.NormalizedCaseNumber.PortalNumber);
        Assert.Equal(11000000, res2.Case.CommissionId);

        // 3. Short shape: CC/599/2017
        var shortJson = """
        {"status": 200, "data": {"caseNumber": "CC/599/2017", "caseStage": "PENDING"}}
        """;
        var client3 = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(shortJson, Encoding.UTF8, "application/json")
        }));
        var res3 = await client3.GetCaseStatusAsync("CC/599/2017");
        Assert.True(res3.IsFound);
        Assert.Equal("CC/599/2017", res3.Case!.NormalizedCaseNumber.Display);
    }

    [Fact]
    public async Task GetCaseStatusAsync_400_is_treated_as_candidate_not_found_without_retrying()
    {
        var callCount = 0;
        var notFoundPayload = """
        {
            "message": "Case Details are not available for this case number!",
            "status": 400,
            "data": null,
            "error": "Case Details are not available for this case number!"
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            callCount++;
            var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(notFoundPayload, Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("X-Exit-Address", "10.0.0.1:8080");
            return Task.FromResult(resp);
        });

        var result = await client.GetCaseStatusAsync("CC/999/2099");

        Assert.Equal(1, callCount); // Must not retry on 400!
        Assert.True(result.IsNotFoundCandidate);
        Assert.Equal("10.0.0.1:8080", result.ExitAddress);
        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanConfirmNotFound_requires_two_independent_exit_addresses()
    {
        var resA = ConsumerCaseLookupResult.NotFoundCandidate("Not found", exitAddress: "proxy-1.internal:8080");
        var resB = ConsumerCaseLookupResult.NotFoundCandidate("Not found", exitAddress: "proxy-2.internal:8080");
        var resSame = ConsumerCaseLookupResult.NotFoundCandidate("Not found", exitAddress: "proxy-1.internal:8080");
        var resFound = ConsumerCaseLookupResult.Found(new ConsumerCaseDetails(
            "DC/1/CC/1/2020",
            MCAROC_Analysis.Services.LitigationModule.Normalisation.CaseNumberNormaliser.Parse(new("CC/1/2020")),
            null, null, null, null, null, null, null, null, null, [], [], [], null, null, null, null, null, false, null, null, false, false),
            exitAddress: "proxy-2.internal:8080");

        Assert.True(ConsumerCaseLookupResult.CanConfirmNotFound(resA, resB));
        Assert.False(ConsumerCaseLookupResult.CanConfirmNotFound(resA, resSame));
        Assert.False(ConsumerCaseLookupResult.CanConfirmNotFound(resA, resFound));
    }

    // ── 2. getCauseTitleListByCompany Endpoint Tests ─────────────────────────────

    [Fact]
    public async Task SearchByCompanyAsync_parses_all_15_fields_and_enriches_commission()
    {
        var json = """
        {
            "data": [
                {
                    "case_number": "DC/497/CC/599/2017",
                    "file_application_number": "21/481/CC/599/2017",
                    "case_stage_name": "ALLOWED",
                    "case_filing_date": "2017-09-28",
                    "date_of_hearing": "2017-10-16",
                    "date_of_next_hearing": "2025-11-27",
                    "date_of_disposal": "2025-11-27",
                    "hearing_sequence_number": 2,
                    "complainant_name": "Complainant ABC",
                    "complainant_advocate_name": "ADV X",
                    "respondent_name": "Company XYZ Ltd",
                    "respondent_advocate_name": "ADV Y",
                    "case_type_name": "Consumer Case",
                    "case_category_name": "HOUSING",
                    "commission_name": "Thane"
                }
            ]
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("getCauseTitleListByCompany", req.RequestUri!.ToString());
            Assert.Contains("commissionTypeId=3", req.RequestUri!.ToString());
            Assert.Contains("commissionId=12270497", req.RequestUri!.ToString());
            Assert.Contains("filingDate1=2015-01-01", req.RequestUri!.ToString());
            Assert.Contains("filingDate2=2025-12-31", req.RequestUri!.ToString());
            Assert.Contains("complainant_respondent_name_en=Company%20XYZ", req.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.SearchByCompanyAsync(
            3,
            12270497,
            "Company XYZ",
            new DateOnly(2015, 1, 1),
            new DateOnly(2025, 12, 31));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Cases);

        var c = result.Cases[0];
        Assert.Equal("DC/497/CC/599/2017", c.CaseNumber);
        Assert.Equal("21/481/CC/599/2017", c.FileApplicationNumber);
        Assert.Equal("ALLOWED", c.CaseStageName);
        Assert.Equal(new DateOnly(2017, 9, 28), c.CaseFilingDate);
        Assert.Equal(new DateOnly(2017, 10, 16), c.DateOfHearing);
        Assert.Equal(new DateOnly(2025, 11, 27), c.DateOfNextHearing);
        Assert.Equal(new DateOnly(2025, 11, 27), c.DateOfDisposal);
        Assert.Equal(2, c.HearingSequenceNumber);
        Assert.Equal("Complainant ABC", c.ComplainantName);
        Assert.Equal("ADV X", c.ComplainantAdvocateName);
        Assert.Equal("Company XYZ Ltd", c.RespondentName);
        Assert.Equal("ADV Y", c.RespondentAdvocateName);
        Assert.Equal("Consumer Case", c.CaseTypeName);
        Assert.Equal("HOUSING", c.CaseCategoryName);
        Assert.Equal("Thane", c.CommissionName);

        // Enriched context from registry
        Assert.Equal("MAHARASHTRA", c.StateName);
        Assert.Equal("Thane", c.DistrictName);
        Assert.Equal(12270497, c.CommissionId);
        Assert.Equal(3, c.CommissionTypeId);
        Assert.Equal(new DateOnly(2025, 11, 27), c.EffectiveHearingDate);
    }

    [Fact]
    public async Task SearchByCompanyAsync_accepts_bare_array_payload()
    {
        var bareListJson = """
        [
            {"case_number": "NC/RP/1/2020", "respondent_name": "Company XYZ"}
        ]
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bareListJson, Encoding.UTF8, "application/json")
        }));

        var result = await client.SearchByCompanyAsync(
            1,
            11000000,
            "Company XYZ",
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Cases);
        Assert.Equal("NC/RP/1/2020", result.Cases[0].CaseNumber);
    }

    // ── 3. getDailyOrderJudgementPdf Endpoint Tests ─────────────────────────────

    [Fact]
    public async Task GetDailyOrderPdfAsync_decodes_base64_pdf()
    {
        var rawBytes = "%PDF-1.7 Test Order Content"u8.ToArray();
        var base64 = Convert.ToBase64String(rawBytes);

        var json = $$"""
        {
            "status": 200,
            "data": {
                "dailyOrderPdf": "{{base64}}"
            }
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("getDailyOrderJudgementPdf", req.RequestUri!.ToString());
            Assert.Contains("filingReferenceNumber=100000651664", req.RequestUri!.ToString());
            Assert.Contains("dateOfHearing=2025-11-27", req.RequestUri!.ToString());
            Assert.Contains("orderTypeId=1", req.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetDailyOrderPdfAsync(100000651664L, new DateOnly(2025, 11, 27));

        Assert.True(result.IsSuccess);
        var doc = result.Document;
        Assert.NotNull(doc);
        Assert.Equal("pdf", doc.ContentType);
        Assert.Equal(".pdf", doc.FileExtension);
        Assert.Equal(rawBytes, doc.Bytes);
        Assert.Equal(100000651664L, doc.FilingReferenceNumber);
        Assert.Equal(new DateOnly(2025, 11, 27), doc.DateOfHearing);
    }

    [Fact]
    public async Task GetDailyOrderPdfAsync_detects_and_preserves_html_note()
    {
        var htmlContent = "<html><head><title>Daily Order</title></head><body>Proceeding notes in HTML</body></html>";
        var json = $$"""
        {
            "status": 200,
            "data": {
                "dailyOrderPdf": "{{htmlContent}}"
            }
        }
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetDailyOrderPdfAsync(100000651664L, new DateOnly(2017, 10, 16));

        Assert.True(result.IsSuccess);
        var doc = result.Document;
        Assert.NotNull(doc);
        Assert.Equal("html", doc.ContentType);
        Assert.Equal(".html", doc.FileExtension);
        Assert.Equal(Encoding.UTF8.GetBytes(htmlContent), doc.Bytes);
    }

    [Fact]
    public async Task GetDailyOrderPdfAsync_400_is_treated_as_not_found_without_retrying()
    {
        var callCount = 0;
        var notFoundPayload = """
        {
            "message": "Daily Order not available.",
            "status": 400,
            "data": null
        }
        """;

        var client = CreateClient((req, ct) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(notFoundPayload, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetDailyOrderPdfAsync(100000651664L, new DateOnly(2018, 5, 22));

        Assert.Equal(1, callCount); // Must not retry on 400
        Assert.True(result.IsNotFound);
        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. Resilience, Cancellation, and Proxy Tests ─────────────────────────────

    [Fact]
    public async Task SendGetAsync_does_not_swallow_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.GetCaseStatusAsync("DC/497/CC/599/2017", cts.Token);
        });
    }

    [Fact]
    public async Task SendGetAsync_honours_retry_after_header_in_full()
    {
        var attempts = 0;
        TimeSpan recordedDelay = TimeSpan.Zero;

        // Custom time provider tracking delays
        var tp = new DelayCapturingTimeProvider(delay => recordedDelay = delay);

        var client = CreateClient((req, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status": 200, "data": {"caseNumber": "CC/1/2020"}}""", Encoding.UTF8, "application/json")
            });
        }, timeProvider: tp);

        var res = await client.GetCaseStatusAsync("CC/1/2020");

        Assert.True(res.IsFound);
        Assert.Equal(2, attempts);
        Assert.Equal(TimeSpan.FromSeconds(120), recordedDelay);
    }

    [Fact]
    public async Task CircuitBreaker_trips_on_threshold_failures_and_blocks_requests()
    {
        var breaker = new ConsumerCircuitBreaker(threshold: 3);

        var client = CreateClient((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }, breaker: breaker);

        for (var i = 0; i < 3; i++)
        {
            await client.GetCaseStatusAsync("CC/1/2020");
        }

        Assert.True(breaker.IsOpen());

        // Next call should be immediately blocked as Unavailable
        var blocked = await client.GetCaseStatusAsync("CC/1/2020");
        Assert.True(blocked.IsUnavailable);
        Assert.Contains("circuit breaker is open", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_requests_receive_independent_proxy_exits_via_provenance()
    {
        var opts = new ConsumerClientOptions
        {
            BaseUrl = "https://e-jagriti.gov.in/",
            RequestsPerSecond = 1000.0,
            ProxyPool = ["http://proxy1:8080", "http://proxy2:8080"]
        };

        var rotator = new ConsumerProxyRotator(opts.ProxyPool, rotateFrequency: 1);
        var handler = ConsumerClient.CreateHandler(opts, rotator);

        var client = CreateClient((req, ct) =>
        {
            // Simulate transport assigning proxy
            rotator.GetProxy(req.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status": 400, "message": "not available"}""", Encoding.UTF8, "application/json")
            });
        }, options: opts, proxy: rotator);

        // Run sequential requests rotating through the pool
        var res1 = await client.GetCaseStatusAsync("CC/1/2020");
        var res2 = await client.GetCaseStatusAsync("CC/2/2020");

        Assert.NotNull(res1.ExitAddress);
        Assert.NotNull(res2.ExitAddress);
        Assert.NotEqual(res1.ExitAddress, res2.ExitAddress);
        Assert.True(ConsumerCaseLookupResult.CanConfirmNotFound(res1, res2));
    }

    [Fact]
    public async Task GetCaseStatusAsync_rejects_response_missing_case_number_and_never_echoes_request()
    {
        var jsonWithoutCaseNumber = """
        {
            "status": 200,
            "data": {
                "complainant": "John Doe",
                "respondent": "Corp Inc",
                "caseStage": "PENDING"
            }
        }
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonWithoutCaseNumber, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetCaseStatusAsync("DC/497/CC/599/2017");

        Assert.True(result.IsError);
        Assert.Null(result.Case);
        Assert.Contains("did not contain caseNumber", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCaseStatusAsync_rejects_case_number_mismatch_between_request_and_response()
    {
        var mismatchJson = """
        {
            "status": 200,
            "data": {
                "caseNumber": "DC/497/CC/600/2017",
                "complainant": "John Doe",
                "respondent": "Corp Inc",
                "caseStage": "PENDING"
            }
        }
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mismatchJson, Encoding.UTF8, "application/json")
        }));

        // Requested 599, but portal returned 600
        var result = await client.GetCaseStatusAsync("DC/497/CC/599/2017");

        Assert.True(result.IsError);
        Assert.Null(result.Case);
        Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("599", result.Message);
        Assert.Contains("600", result.Message);
    }

    [Theory]
    [InlineData("DC/497/CC/599/2017", "DC/497/CC/599/2017", true)]
    [InlineData("CC/599/2017", "DC/497/CC/599/2017", true)]
    [InlineData("NC/RP/836/2005", "NC/RP/836/2005", true)]
    [InlineData("RP/836/2005", "NC/RP/836/2005", true)]
    [InlineData("599/2017", "DC/497/CC/599/2017", false)] // Bare partial number missing case type must be rejected
    [InlineData("DC/497/CC/599/2017", "599/2017", false)]
    [InlineData("DC/497/CC/599/2017", "DC/497/CC/600/2017", false)]
    [InlineData("DC/497/CC/599/2017", "DC/123/CC/599/2017", false)]
    [InlineData("DC/497/CC/599/2017", "DC/497/EA/599/2017", false)]
    [InlineData("DC/497/CC/599/2017", "DC/497/CC/599/2018", false)]
    [InlineData("CC/599/2017", "DC/497/CC/600/2017", false)]
    public void IsCaseNumberMatch_accurately_validates_identity_and_rejects_mismatches(string req, string ret, bool expected)
    {
        Assert.Equal(expected, ConsumerClient.IsCaseNumberMatch(req, ret));
    }

    [Fact]
    public async Task GetCaseStatusAsync_WhenLookupIsPartialNumberWithoutType_RejectsMatchAndReturnsError()
    {
        var responseJson = """
        {
          "status": 200,
          "data": {
            "caseNumber": "DC/497/CC/599/2017",
            "fillingReferenceNumber": 100000651664,
            "caseStage": "DISPOSED",
            "complainant": "JOHN DOE",
            "respondent": "ABC CORP"
          }
        }
        """;

        var client = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        }));

        // Requested bare partial number "599/2017" without establishing case type CC
        var result = await client.GetCaseStatusAsync("599/2017");

        Assert.True(result.IsError);
        Assert.Null(result.Case);
        Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("599/2017", result.Message);
        Assert.Contains("DC/497/CC/599/2017", result.Message);
    }

    private sealed class DelayCapturingTimeProvider : TimeProvider
    {
        private readonly Action<TimeSpan> _onDelay;
        public DelayCapturingTimeProvider(Action<TimeSpan> onDelay) => _onDelay = onDelay;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _onDelay(dueTime);
            // Fire immediately to complete delay
            callback(state);
            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

