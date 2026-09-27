namespace MCAROC_Analysis.Data.Entities;

/// <summary>One word of a company's <c>NameCore</c> → its identifier: the inverted index the name resolver's
/// word-overlap retrieval seeks (issue #294). Derived data, rebuilt by <c>CompanyNameTokenIndex</c>; deliberately
/// no foreign key to <see cref="CompanyMasterRecord"/>, because the bulk import replaces that table by renaming a
/// fresh copy into place, which a foreign key would block.</summary>
public sealed class CompanyNameToken
{
    public string Token { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
}
