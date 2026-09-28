using System.Text.Json;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed record BorrowerIntakeSource(string? SourceStoragePath, string? SourceFileName, string? Text);
public sealed record BorrowerIntakePayload(BorrowerIntakeSource BorrowerIntake);
public sealed record RecognizedBorrowerAssignment(InstaReportData Data, string? MissingDetails);

/// <summary>Recognition happens in the durable worker after creation, not in the upload HTTP request.</summary>
public sealed class BorrowerAssignmentIntake(BorrowerRequestDocumentReader reader, IBorrowerAssignmentAiExtractor ai)
{
    public async Task<RecognizedBorrowerAssignment> RecognizeAsync(BorrowerIntakeSource source, CancellationToken ct)
    {
        var text = source.Text ?? "";
        string? imagePath = null;
        if (source.SourceStoragePath is not null)
        {
            if (Path.GetExtension(source.SourceStoragePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            {
                var image = await SixLabors.ImageSharp.Image.IdentifyAsync(source.SourceStoragePath, ct);
                if (image is null || (long)image.Width * image.Height > 25_000_000 || new FileInfo(source.SourceStoragePath).Length > BorrowerRequestDocumentReader.MaxBytes)
                    throw new PreLoginReportException("Use a screenshot up to 10 MB and 25 megapixels.");
                imagePath = source.SourceStoragePath;
            }
            else
            {
                await using var stream = File.OpenRead(source.SourceStoragePath);
                var file = new FormFile(stream, 0, stream.Length, "requestFile", source.SourceFileName ?? Path.GetFileName(source.SourceStoragePath));
                text = await reader.ReadAsync(file, ct) + "\n" + text;
                if (Path.GetExtension(source.SourceStoragePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) imagePath = source.SourceStoragePath;
            }
        }
        if (text.Length > 100_000) throw new PreLoginReportException("The combined request exceeds 100,000 characters.");
        var (details, model) = await ai.ExtractAsync(text, ct, imagePath);
        var recognized = CreateData(details, source, model);
        if (BorrowerRequestParser.EntityType(details) == PreLoginReportEntityType.Llp)
        {
            var llpin = System.Text.RegularExpressions.Regex.Match(text.ToUpperInvariant(),
                @"\b(?:LLPIN|LLP\s+IDENTIFICATION\s+(?:NO|NUMBER))\s*[:#-]?\s*([A-Z]{3}-[0-9]{4})\b");
            if (llpin.Success && details.CompanyDetails.CompanyName is not null)
                recognized = recognized with { Data = recognized.Data with { McaIdentifier = llpin.Groups[1].Value }, MissingDetails = null };
        }
        return recognized;
    }

    public static RecognizedBorrowerAssignment CreateData(BorrowerAssignmentDetails details, BorrowerIntakeSource source, string model)
    {
        BorrowerAssignmentAiExtractor.Normalize(details);
        var c = details.CompanyDetails;
        var entity = BorrowerRequestParser.EntityType(details);
        var litigationOnly = entity is not (PreLoginReportEntityType.Company or PreLoginReportEntityType.Llp or PreLoginReportEntityType.ForeignCompany);
        // The caller's assignment number is separate from the borrower's optional PAN/registration identity.
        var company = new InstaCompany(c.CompanyName ?? "Unrecognized borrower", "-", c.Pan ?? "-", c.EntityType ?? "Unrecognized entity type",
            "-", "-", "-", "-", "-", c.IncorporationDate ?? "-", c.RegisteredOfficeAddress ?? "-", "-", "-", "-", "-", "-",
            IsPartnership: entity == PreLoginReportEntityType.Partnership, IsLitigationOnly: litigationOnly, EntityType: c.EntityType);
        var missing = new List<string>();
        if (c.CompanyName is null) missing.Add("borrower name");
        if (c.EntityType is null) missing.Add("entity type");
        // LLPIN and foreign registry identifiers are outside the user's CIN-only extraction schema.
        // Preserve the assignment, and require identity resolution instead of passing a PAN as a CIN.
        if (!litigationOnly && c.Cin is null) missing.Add(entity == PreLoginReportEntityType.Llp ? "LLPIN" : entity == PreLoginReportEntityType.ForeignCompany ? "supported MCA identifier for the foreign company" : "CIN");
        return new(new InstaReportData(company, [], [], Assignment: details, SourceFileName: source.SourceFileName,
            SourceStoragePath: source.SourceStoragePath, ExtractionModel: model),
            missing.Count == 0 ? null : "Assignment created; needs " + string.Join(", ", missing) + ".");
    }

    public static BorrowerIntakeSource? PendingSource(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(BorrowerIntakePayload.BorrowerIntake), out _)
            ? JsonSerializer.Deserialize<BorrowerIntakePayload>(json)?.BorrowerIntake : null;
    }
}
