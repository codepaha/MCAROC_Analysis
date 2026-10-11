namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Client contract for DRT / DRAT public portal endpoints (plan sections 2.1, 4.4, 4.5, 4.9).</summary>
public interface IDrtClient
{
    /// <summary>Fetches the list of all 44 tribunals and appellate tribunals (<c>getDrtDratScheamName</c>).</summary>
    Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default);

    /// <summary>Fetches available case types for a specific tribunal schema ID (<c>getDrtDratCaseTyepName</c>).</summary>
    Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default);

    /// <summary>Keyword / LIKE party name search across DRT (<c>casedetail_party_name_wise</c>)
    /// or DRAT (<c>drat_party_name_wise</c> when <paramref name="tribunalId"/> &gt;= 100).</summary>
    Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default);

    /// <summary>Fetches case details by case number, type, and year (<c>getCaseDetailCaseNoWise</c> for DRT,
    /// <c>getDratCaseDetailCaseNoWise</c> for DRAT).</summary>
    Task<DrtCaseLookupResult> GetCaseByNumberAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default);

    /// <summary>Fallback case detail lookup by diary number (<c>getCaseDetailDiaryNoWise</c>).</summary>
    Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default);

    /// <summary>Fetches daily order listings for a case (<c>getDrtDailyOrderReportCaseNo</c>).</summary>
    Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default);

    /// <summary>Fetches final order / judgment listings for a case (<c>getDrtFinalOrderReportCaseNo</c>).
    /// Handles portal response <c>{"status": "Record Not Fund"}</c> as empty.</summary>
    Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default);

    /// <summary>RC/TRC endpoint (<c>getRcTrcCaseStatusReport</c>). Treated as permanently unavailable per plan section 2.1.</summary>
    Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default);

    /// <summary>Active proxy exit address or direct interface through which requests are sent (plan section 4.9).</summary>
    string CurrentExit { get; }

    /// <summary>Downloads and cleans an order document from direct URL or viewer URL.</summary>
    Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20 * 1024 * 1024, CancellationToken ct = default);
}

