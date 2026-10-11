namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Client interface for the e-Jagriti consumer disputes redressal commission portal (plan sections 2.1, 4.4, 4.5, 4.9).
/// Performs resilient, rate-limited requests returning plain records without database dependencies.</summary>
public interface IConsumerClient
{
    /// <summary>The active exit address or proxy URL currently configured on the transport.</summary>
    string CurrentExit { get; }

    /// <summary>The configured proxy rotator instance.</summary>
    ConsumerProxyRotator Rotator { get; }

    /// <summary>Looks up a case directly by its case number via the fast getCaseStatus API.</summary>
    /// <param name="caseNumber">The case number (e.g. "DC/497/CC/599/2017", "NC/RP/836/2005", or "CC/599/2017").</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Searches for cases involving a corporate name or keyword in one commission and date range (getCauseTitleListByCompany).</summary>
    /// <param name="commissionTypeId">Commission type (1=NCDRC, 2=SCDRC, 3=DCDRC).</param>
    /// <param name="commissionId">e-Jagriti backend commission ID (e.g. 11000000, 12270497).</param>
    /// <param name="keyword">Company name or substring to match in English.</param>
    /// <param name="fromDate">Start of filing date range.</param>
    /// <param name="toDate">End of filing date range.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConsumerCompanySearchResult> SearchByCompanyAsync(
        int commissionTypeId,
        int commissionId,
        string keyword,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default);

    /// <summary>Downloads the daily order or judgment document for a specific hearing (getDailyOrderJudgementPdf).</summary>
    /// <param name="filingReferenceNumber">Filing reference number of the case.</param>
    /// <param name="dateOfHearing">Hearing date (IST).</param>
    /// <param name="orderTypeId">Order type ID (default 1 for daily order).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(
        long filingReferenceNumber,
        DateOnly dateOfHearing,
        int orderTypeId = 1,
        CancellationToken ct = default);
}

