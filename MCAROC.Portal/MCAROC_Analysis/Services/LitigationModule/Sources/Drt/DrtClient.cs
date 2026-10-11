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
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _sendHook;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private long _lastRequestTimestamp;
    private int _requestCount;

    public DrtClient(
        HttpClient http,
        IOptions<DrtClientOptions> options,
        ILogger<DrtClient> logger,
        TimeProvider? timeProvider = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHook = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sendHook = sendHook;

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeType"] = "1"
        });

        var json = await SendPostAsync("getDrtDratScheamName", content, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
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
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture)
        });

        var json = await SendPostAsync("getDrtDratCaseTyepName", content, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
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

        var isDrat = tribunalId >= 100;
        var endpoint = isDrat ? "drat_party_name_wise" : "casedetail_party_name_wise";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeNameDratDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["partyName"] = partyName.Trim()
        });

        var json = await SendPostAsync(endpoint, content, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
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
        var isDrat = tribunalId >= 100;
        string endpoint;
        FormUrlEncodedContent content;

        if (isDrat)
        {
            // DRAT endpoint expects 'casetype' instead of 'casetypeId'
            endpoint = "getDratCaseDetailCaseNoWise";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
                ["casetype"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
                ["caseNo"] = caseNo.Trim(),
                ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
            });
        }
        else
        {
            endpoint = "getCaseDetailCaseNoWise";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
                ["casetypeId"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
                ["caseNo"] = caseNo.Trim(),
                ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
            });
        }

        var json = await SendPostAsync(endpoint, content, ct);
        return ParseCaseDetailResponse(json, tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public async Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["diaryNo"] = diaryNo.Trim(),
            ["diaryYear"] = diaryYear.ToString(CultureInfo.InvariantCulture)
        });

        var json = await SendPostAsync("getCaseDetailDiaryNoWise", content, ct);
        return ParseCaseDetailResponse(json, tribunalId, null, null, null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(
        int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["caseType"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
            ["caseNo"] = caseNo.Trim(),
            ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
        });

        var json = await SendPostAsync("getDrtDailyOrderReportCaseNo", content, ct);
        return ParseOrdersResponse(json, "daily", tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(
        int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["schemeNameDrtId"] = tribunalId.ToString(CultureInfo.InvariantCulture),
            ["caseType"] = caseTypeId.ToString(CultureInfo.InvariantCulture),
            ["caseNo"] = caseNo.Trim(),
            ["caseYear"] = caseYear.ToString(CultureInfo.InvariantCulture)
        });

        var json = await SendPostAsync("getDrtFinalOrderReportCaseNo", content, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        // Check for pinned misspelling "Record Not Fund"
        if (json.Contains("Record Not Fund", StringComparison.OrdinalIgnoreCase))
            return [];

        return ParseOrdersResponse(json, "final", tribunalId, caseTypeId, caseNo, caseYear);
    }

    /// <inheritdoc />
    public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
    {
        // Per plan section 2.1 & section 4.9: RC/TRC endpoint on DRT portal is known broken (HTTP 500)
        // Treated permanently as unavailable so workers do not hammer it.
        return Task.FromResult(DrtCaseLookupResult.Unavailable("The RC/TRC endpoint is permanently unavailable on the DRT portal."));
    }

    /// <inheritdoc />
    public async Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20 * 1024 * 1024, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderUrl))
            return DrtOrderDownloadResult.Failure("Empty order URL");

        var downloadUrl = DrtOrderKeyHelper.DirectPdfUrl(orderUrl, _options.OrderBaseUrl);

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            await RateLimitAsync(ct);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);

                using var response = _sendHook is not null
                    ? await _sendHook(request, ct)
                    : await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    return DrtOrderDownloadResult.NotAvailable($"Order not found (HTTP {(int)response.StatusCode})");
                }

                if ((int)response.StatusCode is 429 or 502 or 503 or 504 || response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    if (attempt == _options.MaxRetries)
                        return DrtOrderDownloadResult.Failure($"HTTP {(int)response.StatusCode} after {_options.MaxRetries} retries");

                    await BackoffAsync(attempt, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return DrtOrderDownloadResult.Failure($"HTTP {(int)response.StatusCode}");
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength > maxBytes)
                {
                    return DrtOrderDownloadResult.Failure($"File exceeds maximum allowed size ({contentLength} > {maxBytes} bytes)");
                }

                var rawBytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (rawBytes.Length > maxBytes)
                {
                    return DrtOrderDownloadResult.Failure($"File exceeds maximum allowed size ({rawBytes.Length} > {maxBytes} bytes)");
                }

                return DrtOrderCleaner.Clean(rawBytes, downloadUrl);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.MaxRetries)
            {
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
        string? json, int tribunalId, int? requestedTypeId, string? requestedNo, int? requestedYear)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            // Plan section 4.9: HTTP 200 with empty body is a candidate NotFound
            return DrtCaseLookupResult.NotFoundCandidate("Empty response body from DRT portal", json);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // An array with elements or object
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return DrtCaseLookupResult.NotFoundCandidate("Empty array returned", json);
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return DrtCaseLookupResult.NotFoundCandidate("Unexpected non-object response", json);
            }

            // Check if portal returned validation error dictionary like {"casetype": "The casetype field is required."}
            var hasCaseNo = !string.IsNullOrWhiteSpace(TryGetString(root, "caseno"));
            var hasDiaryNo = !string.IsNullOrWhiteSpace(TryGetString(root, "diaryno"));
            var hasPetitioner = !string.IsNullOrWhiteSpace(TryGetString(root, "petitionerName") ?? TryGetString(root, "applicant"));
            var hasStatus = !string.IsNullOrWhiteSpace(TryGetString(root, "casestatus"));

            if (!hasCaseNo && !hasDiaryNo && !hasPetitioner && !hasStatus)
            {
                return DrtCaseLookupResult.NotFoundCandidate("Response missing identifying case fields", json);
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

            return DrtCaseLookupResult.Found(details, json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed JSON response from case lookup");
            return DrtCaseLookupResult.Error("Contract failure: response was not valid JSON", json);
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
                // Single object response
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

    private async Task<string> SendPostAsync(string endpoint, HttpContent content, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            await RateLimitAsync(ct);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = content;

                using var response = _sendHook is not null
                    ? await _sendHook(request, ct)
                    : await _http.SendAsync(request, ct);

                var body = await response.Content.ReadAsStringAsync(ct);

                // Transient HTTP status codes
                if ((int)response.StatusCode is 429 or 502 or 503 or 504 || response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    if (attempt == _options.MaxRetries)
                    {
                        _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint} exhausted {MaxRetries} retries", (int)response.StatusCode, endpoint, _options.MaxRetries);
                        return string.Empty;
                    }

                    await BackoffAsync(attempt, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("HTTP {StatusCode} for endpoint {Endpoint}: {Body}", (int)response.StatusCode, endpoint, body);
                    return string.Empty;
                }

                return body;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Transient error calling {Endpoint}, attempt {Attempt}", endpoint, attempt);
                await BackoffAsync(attempt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error calling {Endpoint}", endpoint);
                return string.Empty;
            }
        }

        return string.Empty;
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
            _requestCount++;
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

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or TaskCanceledException;

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
}

