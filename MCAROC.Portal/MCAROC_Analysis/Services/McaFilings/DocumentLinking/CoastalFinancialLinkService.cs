using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public static class CoastalFinancialLinkService
{
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CoastalFinancialLinkResult Execute(
        Stream outerZipStream,
        IReadOnlyList<FinancialYearData> financialYears,
        IReadOnlyList<FinancialFact> financialFacts,
        FinancialLinkTargetCatalog targetCatalog,
        CoastalInventoryResult? prebuiltInventory = null,
        Func<CoastalManifestEntry, Stream?>? pdfStreamAccessor = null)
    {
        var inventory = prebuiltInventory ?? CoastalCorpusInventoryService.BuildManifest(outerZipStream);

        // Build deterministic non-mutating ID maps using ReferenceEqualityComparer
        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps(financialYears, financialFacts);

        // Map nested zip archives for reading PDF streams if no external accessor provided
        using var outerZip = (pdfStreamAccessor is null && outerZipStream.CanSeek)
            ? new ZipArchive(outerZipStream, ZipArchiveMode.Read, leaveOpen: true)
            : null;

        var nestedZipBytesCache = new Dictionary<string, byte[]>();

        byte[]? GetNestedZipBytes(string outerEntryPath)
        {
            if (outerZip is null) return null;
            if (nestedZipBytesCache.TryGetValue(outerEntryPath, out var cached)) return cached;

            var entry = outerZip.GetEntry(outerEntryPath);
            if (entry is null) return null;

            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            nestedZipBytesCache[outerEntryPath] = bytes;
            return bytes;
        }

        Stream? OpenPdfStream(CoastalManifestEntry entry)
        {
            if (pdfStreamAccessor is not null)
            {
                return pdfStreamAccessor(entry);
            }

            var nestedZipBytes = GetNestedZipBytes(entry.OuterEntryFullPath);
            if (nestedZipBytes is null) return null;

            using var innerZip = new ZipArchive(new MemoryStream(nestedZipBytes), ZipArchiveMode.Read);
            var pdfEntry = innerZip.GetEntry(entry.NestedEntryRelativePath);
            if (pdfEntry is null) return null;

            using var ps = pdfEntry.Open();
            var pms = new MemoryStream();
            ps.CopyTo(pms);
            pms.Position = 0;
            return pms;
        }

        var resultEntries = new List<CoastalFinancialLinkResultEntry>(inventory.PdfManifestEntries.Count);

        foreach (var entry in inventory.PdfManifestEntries)
        {
            var fileName = Path.GetFileName(entry.NestedEntryRelativePath);
            var parts = entry.NestedEntryRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var sourceFolder = parts.Length > 2 ? parts[1] : string.Empty;

            var outerCategoryFolder = entry.OuterEntryFullPath.Contains("Incorporation and Other Documents", StringComparison.OrdinalIgnoreCase)
                ? "Incorporation and Other Documents"
                : "Charge Documents Financial Documets";

            // 1. Manifest duplicate bypasses matching hierarchy
            if (!entry.IsCanonical)
            {
                var dupEvidence = new
                {
                    isDuplicate = true,
                    canonicalSha256Hex = entry.CanonicalSha256Hex,
                    canonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    canonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath
                };

                resultEntries.Add(new CoastalFinancialLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotFinancialLinkOutcome.ManifestDuplicateBypassed,
                    Reason = PilotFinancialLinkReason.ManifestDuplicate,
                    IsCanonical = false,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(dupEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // 2. Extract candidate details by reading PDF text in memory
            using var pdfStream = OpenPdfStream(entry);
            var candidate = FinancialCandidateExtractor.Extract(outerCategoryFolder, sourceFolder, fileName, pdfStream);

            // If classification (filename, text header, or folder) determined non-financial
            if (!candidate.IsFinancial)
            {
                var outOfScopeEvidence = new
                {
                    classificationCategory = candidate.Category.ToString(),
                    classificationMethod = candidate.Method,
                    classificationConfidence = candidate.Confidence.ToString(),
                    outerCategoryFolder = outerCategoryFolder,
                    sourceFolder = sourceFolder,
                    fileName = fileName
                };

                resultEntries.Add(new CoastalFinancialLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotFinancialLinkOutcome.UnlinkedOutOfScope,
                    Reason = PilotFinancialLinkReason.NonFinancialDocument,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(outOfScopeEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // 4. Perform period and corroboration matching against targetCatalog
            var match = FinancialPeriodMatcher.Match(candidate, targetCatalog, finMap, factMap);

            var evidence = new
            {
                outcome = match.Outcome.ToString(),
                reason = match.Reason.ToString(),
                financialYear = match.MatchedFinancialYear,
                basis = match.MatchedBasis?.ToString(),
                targetKind = match.TargetKind?.ToString(),
                targetEntityId = match.TargetEntityId,
                targetCoordinates = match.TargetCoordinates,
                targetLineItem = match.TargetLineItem,
                matchedValue = match.MatchedValue,
                corroboratedAmount = candidate.CorroboratedAmount,
                corroboratedUnit = candidate.CorroboratedUnit,
                evidencePageNumber = match.EvidencePageNumber,
                evidenceTextQuote = match.EvidenceTextQuote,
                isXfaPlaceholder = candidate.IsXfaPlaceholder
            };

            resultEntries.Add(new CoastalFinancialLinkResultEntry
            {
                OuterEntryFullPath = entry.OuterEntryFullPath,
                NestedEntryRelativePath = entry.NestedEntryRelativePath,
                Sha256Hex = entry.Sha256Hex,
                Outcome = match.Outcome,
                Reason = match.Reason,
                MatchedFinancialYear = match.MatchedFinancialYear,
                MatchedBasis = match.MatchedBasis,
                TargetKind = match.TargetKind,
                TargetEntityId = match.TargetEntityId,
                TargetCoordinates = match.TargetCoordinates,
                TargetLineItem = match.TargetLineItem,
                MatchedValue = match.MatchedValue,
                EvidencePageNumber = match.EvidencePageNumber,
                EvidenceTextQuote = match.EvidenceTextQuote,
                IsCanonical = true,
                CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                EvidenceJson = JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)
            });
        }

        return new CoastalFinancialLinkResult
        {
            Entries = resultEntries
        };
    }
}
