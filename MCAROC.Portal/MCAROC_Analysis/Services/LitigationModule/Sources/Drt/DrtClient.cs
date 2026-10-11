using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Production-ready DRT / DRAT source client (plan sections 2.1, 4.4, 4.5, 4.9).
/// Performs rate-limited, resilient calls to the public DRT API and CIS order server.
/// Returns plain parsed records without database dependencies.</summary>
public sealed class DrtClient : IDrtClient
{
    private readonly HttpClient _http;
    private readonly DrtClientOptions _options;
    private readonly ILogger<DrtClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IDrtCircuitBreaker? _breaker;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _sendHook;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private readonly DrtProxyRotator _rotator;
    private long _lastRequestTimestamp;

    public DrtClient(
        HttpClient http,
        IOptions<DrtClientOptions> options,
        ILogger<DrtClient> logger,
        TimeProvider? timeProvider = null,
        IDrtCircuitBreaker? breaker = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHook = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _breaker = breaker;
        _sendHook = sendHook;
        _rotator = new DrtProxyRotator(_options.ProxyPool, _options.ProxyRotationFrequency);

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    /// <inheritdoc />
    public string CurrentExit => _rotator.CurrentExit;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("DRT circuit breaker is open; skipping GetTribunalsAsync");
            return [];
        }

        var formFields = new Dictionary<string, string>
        {
            ["schemeType"] = "1"
        };

        var response = await SendPostAsync("getDrtDratScheamName", formFields, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) return [];

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var list = new List<DrtTribunal>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = TryGetInt(el, "schemeNameDrtId") ?? 0;
                var rawName = TryGetString(el, "SchemaName") ?? string.Empty;
                if (id <= 0) continue;

                var known = DrtTribunalRegistry.TryGetById(id) ?? DrtTribunalRegistry.Resolve(rawName);
                if (known is not null)
                {
                    list.Add(known);
                }
                else
                {
                    var isDrat = DrtTribunalRegistry.IsDrat(id) || DrtTribunalRegistry.IsDrat(rawName);
                    list.Add(new DrtTribunal(id, rawName, isDrat, string.Empty, string.Empty, rawName));
                }
            }
            return list;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tribunal response from DRT portal");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("DRT circuit breaker is open; skipping GetCaseTypesAsync for {TribunalId}", tribunalId);
            return [];
        }

        var formFields = new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture)
        };

        var response = await SendPostAsync("getDrtDratCaseTyepName", formFields, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) return [];

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var list = new List<DrtCaseType>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = TryGetInt(el, "caseType") ?? 0;
                var name = TryGetString(el, "caseTypeName") ?? string.Empty;
                if (id <= 0 && string.IsNullOrWhiteSpace(name)) continue;

                var code = CaseTypeCodes.Resolve(name);
                list.Add(new DrtCaseType(id, name, code));
            }
            return list;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse case types for tribunal {TribunalId}", tribunalId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partyName)) return [];

        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("DRT circuit breaker is open; skipping SearchByPartyNameAsync for {TribunalId}", tribunalId);
            return [];
        }

        var isDrat = tribunalId >= 100;
        var endpoint = isDrat ? "drat_party_name_wise" : "casedetail_party_name_wise";

        var formFields = new Dictionary<string, string>
        {
            ["schemeNameDratDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["partyName"] = partyName.Trim()
        };

        var response = await SendPostAsync(endpoint, formFields, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) return [];

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var list = new List<DrtPartySearchResult>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var caseType = TryGetString(el, "casetype");
                var caseNo = TryGetString(el, "caseno");
                var diaryNo = TryGetString(el, "diaryno");
                var filingDateStr = TryGetString(el, "dateoffiling");
                var applicant = TryGetString(el, "applicant");
                var respondent = TryGetString(el, "respondent");
                var petAdvocate = TryGetString(el, "petitionerAdvocate") ?? TryGetString(el, "petAdvocate");
                var resAdvocate = TryGetString(el, "respondentAdvocate") ?? TryGetString(el, "resAdvocate");
                var filingNo = TryGetString(el, "filingNo");

                var filingDate = CaseDates.ParseDate(filingDateStr);
                var normalisedNo = CaseNumberNormaliser.Parse(new CaseNumberInput(
                    CaseNumber: caseNo,
                    CaseType: caseType,
                    FilingDate: filingDateStr));

                list.Add(new DrtPartySearchResult(
                    TribunalId: tribunalId,
                    RawCaseType: caseType,
                    RawCaseNo: caseNo,
                    CaseNumber: normalisedNo,
                    DiaryNo: diaryNo,
                    DateOfFiling: filingDate,
                    Applicant: applicant,
                    Respondent: respondent,
                    PetitionerAdvocate: petAdvocate,
                    RespondentAdvocate: resAdvocate,
                    FilingNo: filingNo));
            }
            return list;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse party search results for tribunal {TribunalId}", tribunalId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<DrtCaseLookupResult> GetCaseByNumberAsync(
        int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            return DrtCaseLookupResult.Unavailable("DRT circuit breaker is open due to consecutive portal failures.");
        }

        var isDrat = tribunalId >= 100;
        string endpoint;
        Dictionary<string, string> formFields;

        if (isDrat)
        {
            // DRAT endpoint expects 'casetype' instead of 'casetypeId'
            endpoint = "getDratCaseDetailCaseNoWise";
            formFields = new Dictionary<string, string>
            {
                ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
                ["casetype"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
                ["caseNo"] = caseNo.Trim(),
                ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
            };
        }
        else
        {
            endpoint = "getCaseDetailCaseNoWise";
            formFields = new Dictionary<string, string>
            {
                ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
                ["casetypeId"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
                ["caseNo"] = caseNo.Trim(),
                ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
            };
        }

        var response = await SendPostAsync(endpoint, formFields, ct);
        return ParseCaseDetailResponse(response, tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public async Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            return DrtCaseLookupResult.Unavailable("DRT circuit breaker is open due to consecutive portal failures.");
        }

        var formFields = new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["diaryNo"] = diaryNo.Trim(),
            ["diaryYear"] = diaryYear.ToString(CultureInfo.InvariantCulture)
        };

        var response = await SendPostAsync("getCaseDetailDiaryNoWise", formFields, ct);
        return ParseCaseDetailResponse(response, tribunalId, null, null, null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(
        int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("DRT circuit breaker is open; skipping GetDailyOrdersAsync");
            return [];
        }

        var formFields = new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["caseType"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
            ["caseNo"] = caseNo.Trim(),
            ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
        };

        var response = await SendPostAsync("getDrtDailyOrderReportCaseNo", formFields, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) return [];

        return ParseOrdersResponse(response.Body, "daily", tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(
        int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        if (_breaker?.IsOpen() == true)
        {
            _logger.LogWarning("DRT circuit breaker is open; skipping GetFinalOrdersAsync");
            return [];
        }

        var formFields = new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["caseType"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
            ["caseNo"] = caseNo.Trim(),
            ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
        };

        var response = await SendPostAsync("getDrtFinalOrderReportCaseNo", formFields, ct);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) return [];

        // Check for pinned misspelling "Record Not Fund"
        if (response.Body.Contains("Record Not Fund", StringComparison.OrdinalIgnoreCase))
            return [];

        return ParseOrdersResponse(response.Body, "final", tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        // Per plan section 2.1 & section 4.9: RC/TRC endpoint on DRT portal is known broken (HTTP 500)
        // Treated permanently as unavailable so workers do not hammer it.
        return Task.FromResult(DrtCaseLookupResult.Unavailable("The RC/TRC endpoint is permanently unavailable on the DRT portal.", null, HttpStatusCode.InternalServerError, CurrentExit));
    }

    /// <inheritdoc />
    public async Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20 * 1024 * 1024, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(orderUrl))
            return DrtOrderDownloadResult.Failure("Empty order URL");

        if (_breaker?.IsOpen() == true)
        {
            return DrtOrderDownloadResult.Failure("DRT circuit breaker is open");
        }

        var downloadUrl = DrtOrderKeyHelper.DirectPdfUrl(orderUrl, _options.OrderBaseUrl);

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

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);

                using var response = _sendHook is not null
                    ? await _sendHook(request, timeoutCts.Token)
                    : await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    _breaker?.RecordSuccess();
                    return DrtOrderDownloadResult.NotAvailable($"Order not found (HTTP {(int)response.StatusCode})");
                }

                if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                {
                    _breaker?.RecordFailure(response.StatusCode);
                    if (attempt == _options.MaxRetries)
                        return DrtOrderDownloadResult.Failure($"HTTP {(int)response.StatusCode} after {_options.MaxRetries} retries");

                    await BackoffAsync(attempt, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return DrtOrderDownloadResult.Failure($"HTTP {(int)response.StatusCode}");
                }

                _breaker?.RecordSuccess();

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength > maxBytes)
                {
                    return DrtOrderDownloadResult.Failure($"File exceeds maximum allowed size ({contentLength} > {maxBytes} bytes)");
                }

                var rawBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                if (rawBytes.Length > maxBytes)
                {
                    return DrtOrderDownloadResult.Failure($"File exceeds maximum allowed size ({rawBytes.Length} > {maxBytes} bytes)");
                }

                return DrtOrderCleaner.Clean(rawBytes, downloadUrl);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // propagate caller cancellation immediately
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < _options.MaxRetries)
            {
                _breaker?.RecordFailure(HttpStatusCode.ServiceUnavailable);
                _logger.LogWarning(ex, "Transient error downloading order {Url}, attempt {Attempt}", downloadUrl, attempt);
                await BackoffAsync(attempt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed downloading order {Url}", downloadUrl);
                return DrtOrderDownloadResult.Failure(ex.Message);
            }
        }

        return DrtOrderDownloadResult.Failure("All retries exhausted");
    }

    private DrtCaseLookupResult ParseCaseDetailResponse(
        DrtApiResponse apiResponse, int tribunalId, int? requestedTypeId, string? requestedNo, int? requestedYear)
    {
        // Finding 1: Preserve HTTP failures and do not classify them as NotFoundCandidate!
        if (!apiResponse.IsSuccess)
        {
            var code = (int)apiResponse.StatusCode;
            if (code is 429 or 500 or 502 or 503 or 504)
            {
                return DrtCaseLookupResult.Unavailable(
                    $"Service unavailable (HTTP {code}): {apiResponse.ErrorMessage}",
                    apiResponse.Body,
                    apiResponse.StatusCode,
                    apiResponse.ExitAddress);
            }

            return DrtCaseLookupResult.Error(
                $"HTTP {code}: {apiResponse.ErrorMessage}",
                apiResponse.Body,
                apiResponse.StatusCode,
                apiResponse.ExitAddress);
        }

        var json = apiResponse.Body;
        if (string.IsNullOrWhiteSpace(json))
        {
            // Plan section 4.9: HTTP 200 with empty body is a candidate NotFound
            return DrtCaseLookupResult.NotFoundCandidate(
                "Empty response body from DRT portal", json, apiResponse.ExitAddress);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // An array with elements or object
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return DrtCaseLookupResult.NotFoundCandidate("Empty array returned", json, apiResponse.ExitAddress);
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return DrtCaseLookupResult.NotFoundCandidate("Unexpected non-object response", json, apiResponse.ExitAddress);
            }

            // Check if portal returned validation error dictionary like {"casetype": "The casetype field is required."}
            var hasCaseNo = !string.IsNullOrWhiteSpace(TryGetString(root, "caseno"));
            var hasDiaryNo = !string.IsNullOrWhiteSpace(TryGetString(root, "diaryno"));
            var hasPetitioner = !string.IsNullOrWhiteSpace(TryGetString(root, "petitionerName") ?? TryGetString(root, "applicant"));
            var hasStatus = !string.IsNullOrWhiteSpace(TryGetString(root, "casestatus"));

            if (!hasCaseNo && !hasDiaryNo && !hasPetitioner && !hasStatus)
            {
                return DrtCaseLookupResult.NotFoundCandidate("Response missing identifying case fields", json, apiResponse.ExitAddress);
            }

            var caseNo = TryGetString(root, "caseno") ?? requestedNo;
            var caseType = TryGetString(root, "casetype");
            var caseTypeId = TryGetInt(root, "casetypeId") ?? requestedTypeId;
            var caseYear = TryGetInt(root, "caseyear") ?? requestedYear;
            var diaryNo = TryGetString(root, "diaryno");
            var diaryYear = TryGetInt(root, "diaryyear");
            var filingNo = TryGetString(root, "filingNo");
            var filingDateStr = TryGetString(root, "dateoffiling");
            var filingDate = CaseDates.ParseDate(filingDateStr);

            // Portal raw status: "DISPOSAL", "PENDING", "TRANSFERED" [sic], etc.
            var rawStatus = TryGetString(root, "casestatus");
            var statusCode = TryGetString(root, "statusCode");
            var disposalDateStr = TryGetString(root, "dateofdisposal");
            var disposalDate = CaseDates.ParseDate(disposalDateStr);
            var disposalNature = TryGetString(root, "disposalNature");
            var nextListingDateStr = TryGetString(root, "nextlistingdate");
            var nextListingDate = CaseDates.ParseDate(nextListingDateStr);
            var nextListingPurpose = TryGetString(root, "nextListingPurpose");

            var statusParts = CaseStatusVocabulary.Map(rawStatus, nextListingPurpose);

            var courtNo = TryGetString(root, "courtNo");
            var courtName = TryGetString(root, "courtName");
            var mainCaseNo = TryGetString(root, "maincasecaseno");

            decimal? suitAmount = null;
            if (TryGetString(root, "suit_amount") is { } saStr &&
                decimal.TryParse(saStr.Replace(",", "").Trim(), NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sa))
            {
                suitAmount = sa;
            }

            var petitioner = TryGetString(root, "petitionerName") ?? TryGetString(root, "applicant");
            var respondent = TryGetString(root, "respondentName") ?? TryGetString(root, "respondent");
            var petAddress = TryGetString(root, "petitionerAddress") ?? TryGetString(root, "petAddress");
            var resAddress = TryGetString(root, "respondentAddress") ?? TryGetString(root, "resAddress");
            var petAdvocate = TryGetString(root, "petitionerAdvocate") ?? TryGetString(root, "petAdvocate");
            var resAdvocate = TryGetString(root, "respondentAdvocate") ?? TryGetString(root, "resAdvocate");
            var addPet = TryGetString(root, "additionalpartypet");
            var addRes = TryGetString(root, "additionalpartyres");
            var iaDetails = TryGetString(root, "iaDetails");

            var typeCode = CaseTypeCodes.Resolve(caseType);
            var caseNumber = CaseNumberNormaliser.Parse(new CaseNumberInput(
                CaseNumber: caseNo,
                CaseType: caseType,
                CaseYear: caseYear?.ToString(CultureInfo.InvariantCulture),
                FilingDate: filingDateStr));

            var hearings = new List<DrtHearing>();
            if (root.TryGetProperty("caseProceedingDetails", out var procEl) && procEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in procEl.EnumerateArray())
                {
                    var cDate = CaseDates.ParseDate(TryGetString(h, "causelistdate"));
                    var purpose = TryGetString(h, "purpose");
                    var hCourtName = TryGetString(h, "courtName");
                    var hCourtNo = TryGetString(h, "courtNo");
                    var asCourt = TryGetString(h, "ascourtName");
                    var orderUrl = TryGetString(h, "orderUrl") ?? TryGetString(h, "dailyOrderPdf");
                    var orderKey = !string.IsNullOrWhiteSpace(orderUrl) ? DrtOrderKeyHelper.ExtractOrderKey(orderUrl) : null;

                    hearings.Add(new DrtHearing(cDate, purpose, hCourtName, hCourtNo, asCourt, orderUrl, orderKey));
                }
            }

            var details = new DrtCaseDetails(
                TribunalId: tribunalId,
                CaseTypeId: caseTypeId,
                CaseType: caseType,
                CaseTypeCode: typeCode,
                CaseNo: caseNumber.No ?? caseNo,
                CaseYear: caseNumber.Year ?? caseYear,
                CaseNumber: caseNumber,
                DiaryNo: diaryNo,
                DiaryYear: diaryYear,
                FilingNo: filingNo,
                DateOfFiling: filingDate,
                Status: rawStatus,
                StatusParts: statusParts,
                StatusCode: statusCode,
                DateOfDisposal: disposalDate,
                DisposalNature: disposalNature,
                NextListingDate: nextListingDate,
                NextListingPurpose: nextListingPurpose,
                CourtNo: courtNo,
                CourtName: courtName,
                MainCaseCaseNo: mainCaseNo,
                SuitAmount: suitAmount,
                PetitionerName: petitioner,
                RespondentName: respondent,
                PetitionerAddress: petAddress,
                RespondentAddress: resAddress,
                PetitionerAdvocate: petAdvocate,
                RespondentAdvocate: resAdvocate,
                AdditionalPetitioner: addPet,
                AdditionalRespondent: addRes,
                Hearings: hearings,
                IaDetails: iaDetails);

            return DrtCaseLookupResult.Found(details, json, apiResponse.ExitAddress);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed JSON response from case lookup");
            return DrtCaseLookupResult.Error("Contract failure: response was not valid JSON", json, apiResponse.StatusCode, apiResponse.ExitAddress);
        }
    }

    private static IReadOnlyList<DrtOrder> ParseOrdersResponse(
        string? json, string orderType, int tribunalId, int caseTypeId, string caseNo, int caseYear)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<DrtOrder>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    var o = ParseOrderElement(el, orderType, tribunalId, caseTypeId, caseNo, caseYear);
                    if (o is not null) list.Add(o);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var o = ParseOrderElement(root, orderType, tribunalId, caseTypeId, caseNo, caseYear);
                if (o is not null) list.Add(o);
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static DrtOrder? ParseOrderElement(
        JsonElement el, string orderType, int tribunalId, int caseTypeId, string caseNo, int caseYear)
    {
        var diaryNo = TryGetString(el, "diaryno");
        var dateOfFiling = CaseDates.ParseDate(TryGetString(el, "dateoffiling"));
        var pronouncedDate = CaseDates.ParseDate(TryGetString(el, "pronouncedDate"));
        var applicant = TryGetString(el, "applicant");
        var respondent = TryGetString(el, "respondent");
        var pronouncedBy = TryGetString(el, "pronouncedBy");
        var url = TryGetString(el, orderType == "final" ? "finalOrderPdf" : "dailyOrderPdf")
               ?? TryGetString(el, "orderUrl")
               ?? TryGetString(el, "dailyOrderPdf");
        var itemNo = TryGetString(el, "item_no");

        if (string.IsNullOrWhiteSpace(url) && dateOfFiling is null && pronouncedDate is null)
            return null;

        var orderDate = pronouncedDate ?? dateOfFiling;
        var orderKey = !string.IsNullOrWhiteSpace(url)
            ? DrtOrderKeyHelper.BuildCompositeOrderKey(tribunalId, caseTypeId, caseNo, caseYear, orderType, orderDate, url)
            : null;

        return new DrtOrder(diaryNo, dateOfFiling, pronouncedDate, applicant, respondent, pronouncedBy, url, orderType, itemNo, orderKey);
    }

    private async Task<DrtApiResponse> SendPostAsync(
        string endpoint, IReadOnlyDictionary<string, string> formFields, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var exitAddress = CurrentExit;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await RateLimitAsync(ct);

            _rotator.NotifyRequest();
            if (attempt > 1) _rotator.Rotate();
            exitAddress = CurrentExit;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_options.TimeoutSeconds > 0)
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                }

                // Finding 2: Create fresh content for EACH attempt so disposed content is never reused!
                using var content = new FormUrlEncodedContent(formFields);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = content;

                using var response = _sendHook is not null
                    ? await _sendHook(request, timeoutCts.Token)
                    : await _http.SendAsync(request, timeoutCts.Token);

                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                // Transient HTTP status codes: retry with backoff
                if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                {
                    _breaker?.RecordFailure(response.StatusCode);

                    if (attempt == _options.MaxRetries)
                    {
                        _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint} exhausted {MaxRetries} retries", (int)response.StatusCode, endpoint, _options.MaxRetries);
                        return new DrtApiResponse(response.StatusCode, body, false, $"Exhausted {_options.MaxRetries} retries on HTTP {(int)response.StatusCode}", exitAddress);
                    }

                    await BackoffAsync(attempt, ct);
                    continue;
                }

                // Permanent non-success (e.g. 400, 403, 404): do not retry!
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint}: {Body}", (int)response.StatusCode, endpoint, body);
                    return new DrtApiResponse(response.StatusCode, body, false, $"HTTP {(int)response.StatusCode}: {body}", exitAddress);
                }

                _breaker?.RecordSuccess();
                return new DrtApiResponse(response.StatusCode, body, true, null, exitAddress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Finding 3: Do NOT swallow caller cancellation!
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < _options.MaxRetries)
            {
                _breaker?.RecordFailure(HttpStatusCode.ServiceUnavailable);
                _logger.LogWarning(ex, "Transient error calling {Endpoint}, attempt {Attempt}", endpoint, attempt);
                await BackoffAsync(attempt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error calling {Endpoint}", endpoint);
                return new DrtApiResponse(HttpStatusCode.ServiceUnavailable, null, false, ex.Message, exitAddress);
            }
        }

        return new DrtApiResponse(HttpStatusCode.ServiceUnavailable, null, false, "All retries exhausted", exitAddress);
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

    private async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        // Exponential backoff with jitter per plan section 4.9: ~2s, ~6s, ~18s
        var baseSeconds = Math.Pow(3, attempt - 1) * 2.0;
        var jitter = Random.Shared.NextDouble() * 1.5;
        var delay = TimeSpan.FromSeconds(baseSeconds + jitter);
        await Task.Delay(delay, _timeProvider, ct);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is HttpRequestException or TimeoutException or TaskCanceledException;
    }

    private static string? TryGetString(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static int? TryGetInt(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
                return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private sealed record DrtApiResponse(
        HttpStatusCode StatusCode,
        string? Body,
        bool IsSuccess,
        string? ErrorMessage,
        string? ExitAddress);
}
