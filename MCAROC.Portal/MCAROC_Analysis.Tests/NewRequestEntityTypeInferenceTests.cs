using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Tests;

/// <summary>New Search never asks for the entity type, so it must only infer one from evidence that settles it:
/// a well-formed CIN / LLPIN, or a company PAN. Anything ambiguous or malformed is a validation error, never a
/// default.</summary>
public class NewRequestEntityTypeInferenceTests
{
    [Theory]
    [InlineData("U24246DL2003PTC118255", null, EntityType.Company)]
    [InlineData(" u24246dl2003ptc118255 ", null, EntityType.Company)]
    [InlineData("AAA-1234", null, EntityType.LLP)]
    [InlineData("AAA-1234", "AAAFE1234F", EntityType.LLP)] // the identifier decides, not the firm PAN
    [InlineData(null, "AABCV6369H", EntityType.Company)]
    [InlineData(null, " aabcv6369h ", EntityType.Company)]
    public void Infers_the_type_only_from_conclusive_evidence(string? cin, string? pan, EntityType expected)
    {
        var (type, error) = RequestsController.InferEntityType(cin, pan);

        Assert.Equal(expected, type);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("AAAFE1234F")] // F = firm: an ordinary partnership firm as well as an LLP
    [InlineData("ABCPE1234F")] // individual
    [InlineData("AAATE1234F")] // trust
    [InlineData("AAAHE1234F")] // HUF
    public void A_non_company_PAN_alone_requires_the_CIN_or_LLPIN(string pan)
    {
        var (type, error) = RequestsController.InferEntityType(null, pan);

        Assert.Null(type);
        Assert.Contains("CIN or LLPIN", error);
    }

    [Theory]
    [InlineData("NOT-A-CIN")]
    [InlineData("U24246DL2003PTC11825")]
    [InlineData("AA-1234")]
    [InlineData("AAA-12345")]
    public void Malformed_identifiers_are_rejected_rather_than_typed(string cin)
    {
        var (type, error) = RequestsController.InferEntityType(cin, "AABCV6369H");

        Assert.Null(type);
        Assert.Contains("valid company CIN", error);
    }

    [Theory]
    [InlineData("AABC6369H")]
    [InlineData("12345ABCDE")]
    public void Malformed_PANs_are_rejected(string pan)
    {
        var (type, error) = RequestsController.InferEntityType(null, pan);

        Assert.Null(type);
        Assert.Contains("PAN must be 10 characters", error);
    }
}
