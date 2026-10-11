using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Production-ready e-Jagriti consumer commission source client (plan sections 2.1, 4.4, 4.5, 4.9).
/// Performs rate-limited, resilient calls to the public e-Jagriti APIs.
/// Returns plain parsed records without database dependencies.</summary>
public sealed class ConsumerClient : IConsumerClient
{
    private readonly HttpClient _http;
    private readonly ConsumerClientOptions _options;
    private readonly ILogger<ConsumerClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IConsumerCircuitBreaker? _breaker;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _sendHook;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private readonly ConsumerProxyRotator _rotator;
    private readonly IWebProxy? _transportProxy;
    private long _lastRequestTimestamp;

    /// <summary>Request option key used to track exit provenance explicitly per request.</summary>
    public static readonly HttpRequestOptionsKey<string> ExitProvenanceKey = new("ConsumerExitAddress");

    public ConsumerClient(
        HttpClient http,
        IOptions<ConsumerClientOptions> options,
        ILogger<ConsumerClient> logger,
        TimeProvider? timeProvider = null,
        IConsumerCircuitBreaker? breaker = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHook = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _breaker = breaker ?? ResolveSharedBreaker(_options, _timeProvider);
        _sendHook = sendHook;
        _transportProxy = TryGetTransportProxy(_http);
        _rotator = _transportProxy as ConsumerProxyRotator ?? new ConsumerProxyRotator(_options.ProxyPool, _options.ProxyRotationFrequency);

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    /// <summary>Exposes the configured proxy rotator instance.</summary>
    public ConsumerProxyRotator Rotator => _rotator;

    /// <summary>Creates a configured SocketsHttpHandler with proxy rotation wired into the transport.</summary>
    public static SocketsHttpHandler CreateHandler(ConsumerClientOptions options, IWebProxy? proxy = null)
    {
        var rotator = proxy ?? (options.ProxyPool.Length > 0 ? new ConsumerProxyRotator(options.ProxyPool, options.ProxyRotationFrequency) : null);
        return new SocketsHttpHandler
        {
            Proxy = rotator,
            UseProxy = rotator is not null
        };
    }

    /// <summary>Creates an HttpClient configured with proxy rotation and base address.</summary>
    public static HttpClient CreateHttpClient(ConsumerClientOptions options, IWebProxy? proxy = null)
    {
        var handler = CreateHandler(options, proxy);
        var client = new HttpClient(handler);
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        }
        return client;
    }

    /// <inheritdoc />
    public string CurrentExit
    {
        get
        {
            if (_transportProxy is ConsumerProxyRotator rotator)
            {
                return rotator.CurrentExit;
            }
            if (_transportProxy is WebProxy webProxy && webProxy.Address is not null)
            {
                return webProxy.Address.ToString();
            }
            return "direct";
        }
    }

    /// <inheritdoc />
    public async Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            return ConsumerCaseLookupResult.Error("Case number cannot be empty");
        }

        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("Consumer circuit breaker is open; skipping GetCaseStatusAsync for {CaseNumber}", caseNumber);
            return ConsumerCaseLookupResult.Unavailable("Consumer circuit breaker is open");
        }

        var trimmed = caseNumber.Trim();
        var endpoint = $"services/case/caseFilingService/v2/getCaseStatus?caseNumber={Uri.EscapeDataString(trimmed)}";

        var response = await SendGetAsync(endpoint, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Confirmed portal behavior: HTTP 400 is a genuine "Case Details are not available"
            var msg = "Case Details are not available for this case number!";
            if (!string.IsNullOrWhiteSpace(response.Body))
            {
                try
                {
                    using var doc400 = JsonDocument.Parse(response.Body);
                    if (doc400.RootElement.TryGetProperty("message", out var mEl))
                    {
                        msg = mEl.GetString() ?? msg;
                    }
                }
                catch
                {
                    // Ignore JSON parsing errors for 400 error body
                }
            }
            return ConsumerCaseLookupResult.NotFoundCandidate(msg, response.Body, response.ExitAddress);
        }

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            return ConsumerCaseLookupResult.Unavailable(response.ErrorMessage ?? "Request failed", response.Body, response.StatusCode, response.ExitAddress);
        }

        return ParseCaseStatusResponse(response.Body, trimmed, response);
    }

    /// <inheritdoc />
    public async Task<ConsumerCompanySearchResult> SearchByCompanyAsync(
        int commissionTypeId,
        int commissionId,
        string keyword,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("Consumer circuit breaker is open; skipping SearchByCompanyAsync for {Keyword}", keyword);
            return new ConsumerCompanySearchResult([], false, "Consumer circuit breaker is open", CurrentExit);
        }

        var endpoint = $"services/report/report/getCauseTitleListByCompany" +
                       $"?commissionTypeId={commissionTypeId}" +
                       $"&commissionId={commissionId}" +
                       $"&filingDate1={fromDate:yyyy-MM-dd}" +
                       $"&filingDate2={toDate:yyyy-MM-dd}" +
                       $"&complainant_respondent_name_en={Uri.EscapeDataString(keyword ?? string.Empty)}";

        var response = await SendGetAsync(endpoint, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            return new ConsumerCompanySearchResult([], false, response.ErrorMessage ?? "Search request failed", response.ExitAddress);
        }

        return ParseCompanySearchResponse(response.Body, commissionTypeId, commissionId, response.ExitAddress);
    }

    /// <inheritdoc />
    public async Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(
        long filingReferenceNumber,
        DateOnly dateOfHearing,
        int orderTypeId = 1,
        CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("Consumer circuit breaker is open; skipping GetDailyOrderPdfAsync for {FilingRef}", filingReferenceNumber);
            return ConsumerOrderDownloadResult.Unavailable("Consumer circuit breaker is open");
        }

        var endpoint = $"services/courtmaster/courtRoom/judgement/v1/getDailyOrderJudgementPdf" +
                       $"?filingReferenceNumber={filingReferenceNumber}" +
                       $"&dateOfHearing={dateOfHearing:yyyy-MM-dd}" +
                       $"&orderTypeId={orderTypeId}";

        var response = await SendGetAsync(endpoint, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return ConsumerOrderDownloadResult.NotFound("Daily Order not available.", response.Body, response.ExitAddress);
        }

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            return ConsumerOrderDownloadResult.Unavailable(response.ErrorMessage ?? "Document request failed", response.Body, response.StatusCode, response.ExitAddress);
        }

        return ParseDailyOrderResponse(response.Body, filingReferenceNumber, dateOfHearing, orderTypeId, response.ExitAddress);
    }

    private ConsumerCaseLookupResult ParseCaseStatusResponse(string json, string requestedCaseNumber, ApiResponse apiResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            {
                return ConsumerCaseLookupResult.Error("Contract failure: missing or invalid 'data' property", json, apiResponse.StatusCode, apiResponse.ExitAddress);
            }

            var caseNumber = TryGetString(dataEl, "caseNumber");
            if (string.IsNullOrWhiteSpace(caseNumber))
            {
                return ConsumerCaseLookupResult.Error("Contract failure: portal response did not contain caseNumber", json, apiResponse.StatusCode, apiResponse.ExitAddress);
            }

            if (!IsCaseNumberMatch(requestedCaseNumber, caseNumber))
            {
                _logger.LogWarning("Case number mismatch: requested '{Requested}' but portal returned '{Returned}'", requestedCaseNumber, caseNumber);
                return ConsumerCaseLookupResult.Error(
                    $"Case number mismatch: requested '{requestedCaseNumber}' but portal returned '{caseNumber}'",
                    json,
                    apiResponse.StatusCode,
                    apiResponse.ExitAddress);
            }

            // Pin portal misspelling: "fillingReferenceNumber"
            long? filingRefNumber = null;
            if (dataEl.TryGetProperty("fillingReferenceNumber", out var frEl))
            {
                if (frEl.ValueKind == JsonValueKind.Number && frEl.TryGetInt64(out var frVal))
                {
                    filingRefNumber = frVal;
                }
                else if (frEl.ValueKind == JsonValueKind.String && long.TryParse(frEl.GetString(), out var frParsed))
                {
                    filingRefNumber = frParsed;
                }
            }

            // Timestamp parsing: e-Jagriti timestamps are UTC ISO strings ("2017-09-27T18:30:00.000+00:00").
            // CaseDates.ParseDate converts UTC instants to IST calendar dates.
            var caseFilingDate = CaseDates.ParseDate(TryGetString(dataEl, "caseFilingDate"));

            // Pin portal misspelling: "filedInComissionName"
            var filedInCommissionName = TryGetString(dataEl, "filedInComissionName") ?? TryGetString(dataEl, "filedInCommissionName");

            var complainant = TryGetString(dataEl, "complainant");
            var respondent = TryGetString(dataEl, "respondent");
            var caseStage = TryGetString(dataEl, "caseStage");

            // Pin portal misspelling: "dateOfNextearing"
            var dateOfNextHearing = CaseDates.ParseDate(TryGetString(dataEl, "dateOfNextearing") ?? TryGetString(dataEl, "dateOfNextHearing"));

            var complainantAdvocate = FlattenAdvocateNames(dataEl, "complainantAdvocate");
            var respondentAdvocate = FlattenAdvocateNames(dataEl, "respondentAdvocate");

            var applicationsFiled = ExtractStringList(dataEl, "applicationsFiled");
            var attachedLowerCourt = ExtractStringList(dataEl, "attachedLowerCourt");

            var hearings = new List<ConsumerHearing>();
            if (dataEl.TryGetProperty("caseHearingDetails", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hEl.EnumerateArray())
                {
                    var hId = TryGetString(item, "courtRoomHearingId") ?? string.Empty;
                    var hDate = CaseDates.ParseDate(TryGetString(item, "dateOfHearing"));
                    var hNextDate = CaseDates.ParseDate(TryGetString(item, "dateOfNextHearing"));
                    var hStage = TryGetString(item, "caseStage") ?? string.Empty;
                    var hText = TryGetString(item, "proceedingText") ?? string.Empty;
                    var hDailyOrder = item.TryGetProperty("dailyOrderStatus", out var dosEl) &&
                                      ((dosEl.ValueKind == JsonValueKind.True) ||
                                       (dosEl.ValueKind == JsonValueKind.String && bool.TryParse(dosEl.GetString(), out var b) && b));

                    int? hOrderType = null;
                    if (item.TryGetProperty("orderTypeId", out var otEl) && otEl.ValueKind == JsonValueKind.Number && otEl.TryGetInt32(out var otVal))
                    {
                        hOrderType = otVal;
                    }

                    hearings.Add(new ConsumerHearing(hId, hDate, hNextDate, hStage, hText, hDailyOrder, hOrderType, item.GetRawText()));
                }
            }

            // Normalise case number structure (handles DC/497/CC/599/2017, NC/RP/836/2005, CC/599/2017)
            var normParts = CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: caseNumber));

            // Derive commission, state, district from registry
            var derivedCommission = ResolveCommission(caseNumber, filedInCommissionName);
            var commissionTypeId = derivedCommission?.CommissionTypeId;
            var commissionId = derivedCommission?.CommissionId;
            var commissionName = filedInCommissionName ?? derivedCommission?.CommissionName;
            var stateName = derivedCommission?.StateName;
            var districtName = derivedCommission?.DistrictName;
            var isBench = derivedCommission?.IsBench ?? false;

            // Derive decision date for terminal cases
            var (derivedDecisionDate, isDecisionDerived) = ConsumerCaseDetails.DeriveDecisionDate(caseStage, hearings);
            var statusParts = CaseStatusVocabulary.Map(caseStage);
            var isTerminal = ConsumerCaseDetails.IsTerminalStage(caseStage);

            var details = new ConsumerCaseDetails(
                CaseNumber: caseNumber,
                NormalizedCaseNumber: normParts,
                FilingReferenceNumber: filingRefNumber,
                CaseFilingDate: caseFilingDate,
                FiledInCommissionName: filedInCommissionName,
                Complainant: complainant,
                Respondent: respondent,
                CaseStage: caseStage,
                DateOfNextHearing: dateOfNextHearing,
                ComplainantAdvocate: complainantAdvocate,
                RespondentAdvocate: respondentAdvocate,
                ApplicationsFiled: applicationsFiled,
                AttachedLowerCourt: attachedLowerCourt,
                Hearings: hearings,
                CommissionTypeId: commissionTypeId,
                CommissionId: commissionId,
                CommissionName: commissionName,
                StateName: stateName,
                DistrictName: districtName,
                IsBench: isBench,
                StatusParts: statusParts,
                DerivedDecisionDate: derivedDecisionDate,
                IsDecisionDateDerived: isDecisionDerived,
                IsTerminal: isTerminal,
                RawResponse: json,
                ExitAddress: apiResponse.ExitAddress);

            return ConsumerCaseLookupResult.Found(details, json, apiResponse.ExitAddress);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed JSON response from consumer case status lookup");
            return ConsumerCaseLookupResult.Error("Contract failure: response was not valid JSON", json, apiResponse.StatusCode, apiResponse.ExitAddress);
        }
    }

    private static ConsumerCommission? ResolveCommission(string caseNumber, string? courtName)
    {
        // 1. Try extracting prefix from case number (e.g. "DC/497", "NC", "SC/35", "SC/CB1/8")
        var segments = caseNumber.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
        {
            // Test full prefixes: first 1, 2, or 3 segments
            for (var len = Math.Min(3, segments.Length - 1); len >= 1; len--)
            {
                var candidatePrefix = string.Join("/", segments.Take(len));
                var match = ConsumerCommissionRegistry.FindByPrefix(candidatePrefix);
                if (match is not null) return match;
            }
        }

        // 2. Try court display name
        if (!string.IsNullOrWhiteSpace(courtName))
        {
            var match = ConsumerCommissionRegistry.FindByCourtName(courtName);
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>Validates that the returned case number matches the requested case number.
    /// Supports exact matches, matching complete normalized display (TYPE/NUMBER/YEAR),
    /// and suffix matching only when the shorter side is a supported complete shape (TYPE/NUMBER/YEAR)
    /// against a full portal number (e.g. "CC/599/2017" matching "DC/497/CC/599/2017").
    /// Rejects partial numbers (e.g. bare "599/2017" missing case type), mismatched sequence numbers,
    /// years, case types, or conflicting court prefixes.</summary>
    public static bool IsCaseNumberMatch(string requested, string returned)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(returned))
        {
            return false;
        }

        var reqClean = requested.Trim().ToUpperInvariant();
        var retClean = returned.Trim().ToUpperInvariant();

        if (string.Equals(reqClean, retClean, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var reqParts = CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: requested));
        var retParts = CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: returned));

        // Both are complete case numbers (type, number, year)
        if (reqParts.Completeness == CaseNumberCompleteness.Complete &&
            retParts.Completeness == CaseNumberCompleteness.Complete)
        {
            if (string.Equals(reqParts.Display, retParts.Display, StringComparison.OrdinalIgnoreCase))
            {
                // Display matches (e.g. "CC/599/2017"). If both have portal numbers, verify they don't conflict.
                if (!string.IsNullOrWhiteSpace(reqParts.PortalNumber) &&
                    !string.IsNullOrWhiteSpace(retParts.PortalNumber) &&
                    !string.Equals(reqParts.PortalNumber, retParts.PortalNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }
        }

        // If one side is a complete short form (TYPE/NUMBER/YEAR) and the other is a full portal number (DC/... or NC/... or SC/...)
        // verify that the complete short form matches the 3-segment suffix of the full portal number.
        // Partial numbers (e.g. "599/2017" without case type) are explicitly rejected.
        var reqSegs = reqClean.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var retSegs = retClean.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (reqSegs.Length == 3 && retSegs.Length > 3 && reqParts.Completeness == CaseNumberCompleteness.Complete)
        {
            var retSuffix = retSegs.Skip(retSegs.Length - 3).ToArray();
            return reqSegs.SequenceEqual(retSuffix, StringComparer.OrdinalIgnoreCase);
        }

        if (retSegs.Length == 3 && reqSegs.Length > 3 && retParts.Completeness == CaseNumberCompleteness.Complete)
        {
            var reqSuffix = reqSegs.Skip(reqSegs.Length - 3).ToArray();
            return retSegs.SequenceEqual(reqSuffix, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private ConsumerCompanySearchResult ParseCompanySearchResponse(
        string json, int commissionTypeId, int commissionId, string? exitAddress)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement rowsElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                // Bare list returned by some courts for zero-result searches or direct responses
                rowsElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Array)
            {
                rowsElement = dEl;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var nullData) && nullData.ValueKind == JsonValueKind.Null)
            {
                return new ConsumerCompanySearchResult([], true, null, exitAddress);
            }
            else
            {
                return new ConsumerCompanySearchResult([], false, "Contract failure: unexpected response shape (no data array)", exitAddress);
            }

            var commission = ConsumerCommissionRegistry.GetById(commissionId);
            var list = new List<ConsumerCompanyCase>();

            foreach (var row in rowsElement.EnumerateArray())
            {
                var caseNumber = CleanString(TryGetString(row, "case_number"));
                var fileAppNo = CleanString(TryGetString(row, "file_application_number"));
                var stageName = CleanString(TryGetString(row, "case_stage_name"));
                var filingDate = CaseDates.ParseDate(TryGetString(row, "case_filing_date"));
                var hearingDate = CaseDates.ParseDate(TryGetString(row, "date_of_hearing"));
                var nextHearingDate = CaseDates.ParseDate(TryGetString(row, "date_of_next_hearing"));
                var disposalDate = CaseDates.ParseDate(TryGetString(row, "date_of_disposal"));

                int? seqNo = null;
                if (row.TryGetProperty("hearing_sequence_number", out var seqEl))
                {
                    if (seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt32(out var sv)) seqNo = sv;
                    else if (seqEl.ValueKind == JsonValueKind.String && int.TryParse(seqEl.GetString(), out var sp)) seqNo = sp;
                }

                var compName = CleanString(TryGetString(row, "complainant_name"));
                var compAdv = CleanString(TryGetString(row, "complainant_advocate_name"));
                var respName = CleanString(TryGetString(row, "respondent_name"));
                var respAdv = CleanString(TryGetString(row, "respondent_advocate_name"));
                var typeName = CleanString(TryGetString(row, "case_type_name"));
                var catName = CleanString(TryGetString(row, "case_category_name"));
                var commName = CleanString(TryGetString(row, "commission_name")) ?? commission?.CommissionName;

                var effectiveHearing = nextHearingDate ?? hearingDate;
                var normParts = CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: caseNumber, CaseType: typeName));

                list.Add(new ConsumerCompanyCase(
                    CaseNumber: caseNumber ?? string.Empty,
                    FileApplicationNumber: fileAppNo,
                    CaseStageName: stageName,
                    CaseFilingDate: filingDate,
                    DateOfHearing: hearingDate,
                    DateOfNextHearing: nextHearingDate,
                    DateOfDisposal: disposalDate,
                    HearingSequenceNumber: seqNo,
                    ComplainantName: compName,
                    ComplainantAdvocateName: compAdv,
                    RespondentName: respName,
                    RespondentAdvocateName: respAdv,
                    CaseTypeName: typeName,
                    CaseCategoryName: catName,
                    CommissionName: commName,
                    CommissionId: commissionId,
                    CommissionTypeId: commissionTypeId,
                    StateName: commission?.StateName,
                    DistrictName: commission?.DistrictName,
                    EffectiveHearingDate: effectiveHearing,
                    NormalizedCaseNumber: normParts,
                    RawJson: row.GetRawText()));
            }

            return new ConsumerCompanySearchResult(list, true, null, exitAddress);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse company search JSON response");
            return new ConsumerCompanySearchResult([], false, "Malformed JSON response", exitAddress);
        }
    }

    private ConsumerOrderDownloadResult ParseDailyOrderResponse(
        string json, long filingRef, DateOnly hearingDate, int orderTypeId, string? exitAddress)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            {
                return ConsumerOrderDownloadResult.NotFound("No data object in order document response", json, exitAddress);
            }

            var rawPdf = TryGetString(dataEl, "dailyOrderPdf");
            if (string.IsNullOrWhiteSpace(rawPdf))
            {
                return ConsumerOrderDownloadResult.NotFound("No order PDF in response for this hearing", json, exitAddress);
            }

            // Check for portal-generated HTML note (confirmed live for older hearings)
            if (IsHtmlNote(rawPdf))
            {
                var htmlBytes = Encoding.UTF8.GetBytes(rawPdf);
                var docObj = new ConsumerOrderDocument(filingRef, hearingDate, orderTypeId, htmlBytes, "html", exitAddress);
                return ConsumerOrderDownloadResult.Success(docObj, json, exitAddress);
            }

            // Decode base64 PDF
            try
            {
                var pdfBytes = Convert.FromBase64String(rawPdf);
                var docObj = new ConsumerOrderDocument(filingRef, hearingDate, orderTypeId, pdfBytes, "pdf", exitAddress);
                return ConsumerOrderDownloadResult.Success(docObj, json, exitAddress);
            }
            catch (FormatException ex)
            {
                // In case an HTML note failed base64 decoding but didn't have leading whitespace trimmed
                if (IsHtmlNote(rawPdf.Trim()))
                {
                    var htmlBytes = Encoding.UTF8.GetBytes(rawPdf);
                    var docObj = new ConsumerOrderDocument(filingRef, hearingDate, orderTypeId, htmlBytes, "html", exitAddress);
                    return ConsumerOrderDownloadResult.Success(docObj, json, exitAddress);
                }

                _logger.LogWarning(ex, "Failed to decode base64 PDF payload for filingRef={FilingRef}", filingRef);
                return ConsumerOrderDownloadResult.Error($"Could not decode PDF payload: {ex.Message}", json, HttpStatusCode.OK, exitAddress);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse order document JSON");
            return ConsumerOrderDownloadResult.Error("Contract failure: response was not valid JSON", json, HttpStatusCode.OK, exitAddress);
        }
    }

    private static bool IsHtmlNote(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Length >= 500 && trimmed[..500].Contains("<title>Daily Order</title>", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Contains("<title>Daily Order</title>", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<ApiResponse> SendGetAsync(string endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var exitAddress = CurrentExit;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await RateLimitAsync(ct);

            _rotator.NotifyRequest();
            if (attempt > 1) _rotator.Rotate();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_options.TimeoutSeconds > 0)
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                HttpResponseMessage response;
                try
                {
                    ConsumerProxyRotator.CurrentRequest.Value = request;
                    response = _sendHook is not null
                        ? await _sendHook(request, timeoutCts.Token)
                        : await _http.SendAsync(request, timeoutCts.Token);
                }
                finally
                {
                    ConsumerProxyRotator.CurrentRequest.Value = null;
                }

                using (response)
                {
                    exitAddress = DetermineExitAddress(request, response);
                    var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                    // Transient HTTP status codes: retry with backoff and honour Retry-After
                    if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                    {
                        var retryAfter = ParseRetryAfter(response);
                        _breaker?.RecordFailure(response.StatusCode, retryAfter);

                        if (attempt == _options.MaxRetries)
                        {
                            var msg = retryAfter.HasValue
                                ? $"Exhausted {_options.MaxRetries} retries on HTTP {(int)response.StatusCode} (Retry-After: {retryAfter.Value.TotalSeconds:F0}s)"
                                : $"Exhausted {_options.MaxRetries} retries on HTTP {(int)response.StatusCode}";
                            _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint} exhausted {MaxRetries} retries", (int)response.StatusCode, endpoint, _options.MaxRetries);
                            return new ApiResponse(response.StatusCode, body, false, msg, exitAddress);
                        }

                        await BackoffAsync(attempt, retryAfter, ct);
                        continue;
                    }

                    // 400 Bad Request is returned by e-Jagriti for genuine NotFound cases (case not found, order not found)
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        _breaker?.RecordSuccess();
                        return new ApiResponse(response.StatusCode, body, false, "HTTP 400 Bad Request", exitAddress);
                    }

                    // Other permanent client errors (403, 404, etc.): do not retry
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint}: {Body}", (int)response.StatusCode, endpoint, body);
                        return new ApiResponse(response.StatusCode, body, false, $"HTTP {(int)response.StatusCode}: {body}", exitAddress);
                    }

                    _breaker?.RecordSuccess();
                    return new ApiResponse(response.StatusCode, body, true, null, exitAddress);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation: propagate immediately!
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < _options.MaxRetries)
            {
                _breaker?.RecordFailure(HttpStatusCode.ServiceUnavailable);
                _logger.LogWarning(ex, "Transient error calling {Endpoint}, attempt {Attempt}", endpoint, attempt);
                await BackoffAsync(attempt, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error calling {Endpoint}", endpoint);
                return new ApiResponse(HttpStatusCode.ServiceUnavailable, null, false, ex.Message, exitAddress);
            }
        }

        return new ApiResponse(HttpStatusCode.ServiceUnavailable, null, false, "All retries exhausted", exitAddress);
    }

    private static IConsumerCircuitBreaker? ResolveSharedBreaker(ConsumerClientOptions options, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) return null;
        if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri))
        {
            return ConsumerCircuitBreaker.GetShared(uri.Host, timeProvider: timeProvider);
        }
        return null;
    }

    private static IWebProxy? TryGetTransportProxy(HttpClient http)
    {
        try
        {
            var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? typeof(HttpClient).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
            var handler = field?.GetValue(http) as HttpMessageHandler;

            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler;
            }

            if (handler is SocketsHttpHandler socketsHandler)
            {
                return socketsHandler.Proxy;
            }
            if (handler is HttpClientHandler clientHandler)
            {
                return clientHandler.Proxy;
            }
        }
        catch
        {
            // Ignore reflection issues
        }
        return null;
    }

    private string DetermineExitAddress(HttpRequestMessage request, HttpResponseMessage? response = null)
    {
        if (response?.Headers.TryGetValues("X-Exit-Address", out var respExits) == true)
        {
            var custom = respExits.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
        }

        if (request.Headers.TryGetValues("X-Exit-Address", out var customExits))
        {
            var custom = customExits.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
        }

        if (request.Options.TryGetValue(ExitProvenanceKey, out var explicitExit) && !string.IsNullOrWhiteSpace(explicitExit))
        {
            return explicitExit;
        }

        if (_transportProxy is null)
        {
            return "direct";
        }

        if (_transportProxy is ConsumerProxyRotator rotator && rotator.LastQueriedExit is not null)
        {
            return rotator.LastQueriedExit;
        }

        if (_transportProxy is WebProxy webProxy && webProxy.Address is not null)
        {
            return webProxy.Address.ToString();
        }

        return "direct";
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta.HasValue && ra.Delta.Value > TimeSpan.Zero)
            {
                return ra.Delta.Value;
            }
            if (ra.Date.HasValue)
            {
                var diff = ra.Date.Value - _timeProvider.GetUtcNow();
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }
        return null;
    }

    private async Task RateLimitAsync(CancellationToken ct)
    {
        if (_options.RequestsPerSecond <= 0) return;

        var minInterval = TimeSpan.FromSeconds(1.0 / _options.RequestsPerSecond);
        await _throttleLock.WaitAsync(ct);
        try
        {
            var now = _timeProvider.GetTimestamp();
            if (_lastRequestTimestamp > 0)
            {
                var elapsed = _timeProvider.GetElapsedTime(_lastRequestTimestamp, now);
                if (elapsed < minInterval)
                {
                    var delay = minInterval - elapsed;
                    await Task.Delay(delay, _timeProvider, ct);
                }
            }
            _lastRequestTimestamp = _timeProvider.GetTimestamp();
        }
        finally
        {
            _throttleLock.Release();
        }
    }

    private async Task BackoffAsync(int attempt, TimeSpan? serverRetryAfter, CancellationToken ct)
    {
        if (serverRetryAfter.HasValue && serverRetryAfter.Value > TimeSpan.Zero)
        {
            // Honour the full Retry-After value
            await Task.Delay(serverRetryAfter.Value, _timeProvider, ct);
            return;
        }

        var baseMs = (int)(Math.Pow(2, attempt - 1) * 750);
        var jitter = Random.Shared.Next(0, 400);
        var delay = TimeSpan.FromMilliseconds(Math.Min(8000, baseMs + jitter));
        await Task.Delay(delay, _timeProvider, ct);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested && ex is OperationCanceledException) return false;
        return ex is HttpRequestException or TimeoutException or IOException or TaskCanceledException;
    }

    private static string? TryGetString(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString();
            if (p.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return p.GetRawText();
        }
        return null;
    }

    private static string? CleanString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string? FlattenAdvocateNames(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Array)
        {
            var names = p.EnumerateArray()
                .Select(x => x.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractStringList(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var p) || p.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return p.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private sealed record ApiResponse(
        HttpStatusCode StatusCode,
        string? Body,
        bool IsSuccess,
        string? ErrorMessage,
        string? ExitAddress);
}

