using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #293: variants of the same written name must normalize identically; different names must
/// not collapse together. Real names are from the #191/#192 fixtures (Coastal, Kanpur Flowercycling) and the
/// MCA master's own spelling conventions.</summary>
public class CompanyNameNormalizerTests
{
    [Theory]
    [InlineData("ABC Pvt Ltd", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("ABC PRIVATE LIMITED", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("abc pvt. ltd.", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("ABC (P) Ltd", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("ABC P. Ltd.", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("ABC Pvt.Ltd.", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("ABC PvtLtd", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("M/s. ABC Pvt Ltd", "ABC PRIVATE LIMITED", "ABC", EntityForm.Private)]
    [InlineData("Coastal Projects Limited", "COASTAL PROJECTS LIMITED", "COASTAL PROJECTS", EntityForm.Public)]
    [InlineData("COASTAL PROJECTS LTD.", "COASTAL PROJECTS LIMITED", "COASTAL PROJECTS", EntityForm.Public)]
    [InlineData("Kanpur Flowercycling Private Limited", "KANPUR FLOWERCYCLING PRIVATE LIMITED", "KANPUR FLOWERCYCLING", EntityForm.Private)]
    [InlineData("Sharma & Sons Pvt Ltd", "SHARMA AND SONS PRIVATE LIMITED", "SHARMA AND SONS", EntityForm.Private)]
    [InlineData("Sharma and Sons Private Limited", "SHARMA AND SONS PRIVATE LIMITED", "SHARMA AND SONS", EntityForm.Private)]
    [InlineData("Gupta & Co. Ltd", "GUPTA AND COMPANY LIMITED", "GUPTA AND COMPANY", EntityForm.Public)]
    [InlineData("Shree's Textiles Pvt Ltd", "SHREES TEXTILES PRIVATE LIMITED", "SHREES TEXTILES", EntityForm.Private)]
    [InlineData("The Indian Hotels Company Limited", "THE INDIAN HOTELS COMPANY LIMITED", "INDIAN HOTELS COMPANY", EntityForm.Public)]
    public void CompanyVariants(string input, string normalized, string core, EntityForm form)
    {
        var result = CompanyNameNormalizer.Normalize(input);

        Assert.Equal(normalized, result.NameNormalized);
        Assert.Equal(core, result.NameCore);
        Assert.Equal(form, result.EntityForm);
    }

    [Theory]
    [InlineData("Sunrise Advisors LLP", "SUNRISE ADVISORS LLP", "SUNRISE ADVISORS", EntityForm.Llp)]
    [InlineData("Sunrise Advisors L.L.P.", "SUNRISE ADVISORS LLP", "SUNRISE ADVISORS", EntityForm.Llp)]
    [InlineData("Sunrise Advisors Limited Liability Partnership", "SUNRISE ADVISORS LLP", "SUNRISE ADVISORS", EntityForm.Llp)]
    [InlineData("Priya Designs (OPC) Private Limited", "PRIYA DESIGNS OPC PRIVATE LIMITED", "PRIYA DESIGNS", EntityForm.OnePerson)]
    [InlineData("Priya Designs Private Limited (O.P.C.)", "PRIYA DESIGNS PRIVATE LIMITED OPC", "PRIYA DESIGNS", EntityForm.OnePerson)]
    [InlineData("Vidarbha Farmers Producer Company Limited", "VIDARBHA FARMERS PRODUCER COMPANY LIMITED", "VIDARBHA FARMERS", EntityForm.Producer)]
    [InlineData("Acme Holdings Inc.", "ACME HOLDINGS INC", "ACME HOLDINGS", EntityForm.Foreign)]
    [InlineData("Acme Asia Pte. Ltd.", "ACME ASIA PTE LIMITED", "ACME ASIA", EntityForm.Foreign)]
    [InlineData("Nagpur Education Foundation", "NAGPUR EDUCATION FOUNDATION", "NAGPUR EDUCATION FOUNDATION", EntityForm.Unknown)]
    public void OtherForms(string input, string normalized, string core, EntityForm form)
    {
        var result = CompanyNameNormalizer.Normalize(input);

        Assert.Equal(normalized, result.NameNormalized);
        Assert.Equal(core, result.NameCore);
        Assert.Equal(form, result.EntityForm);
    }

    [Theory]
    [InlineData("Anand Co-operative Housing Society Limited")]
    [InlineData("Anand Co Operative Housing Society Ltd")]
    [InlineData("Anand Co-op Housing Society Ltd.")]
    public void CoOperative_IsNotExpandedToCompany(string input) =>
        Assert.Equal("ANAND COOPERATIVE HOUSING SOCIETY LIMITED", CompanyNameNormalizer.Normalize(input).NameNormalized);

    [Fact]
    public void AccentsAndFullWidthCharacters_Fold()
    {
        Assert.Equal("CAFE MOCHA PRIVATE LIMITED", CompanyNameNormalizer.Normalize("Café Mocha Pvt Ltd").NameNormalized);
        Assert.Equal("ABC PRIVATE LIMITED", CompanyNameNormalizer.Normalize("ＡＢＣ Pvt Ltd").NameNormalized);
    }

    [Theory]
    [InlineData("ABC Private Limited", "ABC Limited")]           // different legal forms
    [InlineData("ABC Private Limited", "ABC LLP")]
    [InlineData("Sharma Sons Private Limited", "Sharma and Sons Private Limited")] // a real word difference
    [InlineData("Shree Ram Traders Private Limited", "Ram Shree Traders Private Limited")] // word order is the resolver's job
    public void DifferentNames_StayDifferent(string a, string b) =>
        Assert.NotEqual(CompanyNameNormalizer.Normalize(a).NameNormalized, CompanyNameNormalizer.Normalize(b).NameNormalized);

    [Fact]
    public void SameCore_DifferentForms_ShareNameCore()
    {
        // LLP vs company twins: the core is the same, EntityForm tells them apart.
        var company = CompanyNameNormalizer.Normalize("Sunrise Advisors Private Limited");
        var llp = CompanyNameNormalizer.Normalize("Sunrise Advisors LLP");

        Assert.Equal(company.NameCore, llp.NameCore);
        Assert.NotEqual(company.EntityForm, llp.EntityForm);
    }

    [Theory]
    [InlineData("Limited")]
    [InlineData("LLP")]
    public void SuffixOnlyName_KeepsItsWordAsCore(string input)
    {
        var result = CompanyNameNormalizer.Normalize(input);

        Assert.Equal(result.NameNormalized, result.NameCore);
        Assert.Equal(EntityForm.Unknown, result.EntityForm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".,-")]
    public void BlankOrPunctuationOnly_IsEmpty(string? input)
    {
        var result = CompanyNameNormalizer.Normalize(input);

        Assert.Equal("", result.NameNormalized);
        Assert.Equal("", result.NameCore);
        Assert.Equal(EntityForm.Unknown, result.EntityForm);
    }

    [Fact]
    public void Output_NeverExceedsColumnWidth()
    {
        // 400 characters of "PVT " expand to "PRIVATE " — longer than the stored column.
        var result = CompanyNameNormalizer.Normalize(string.Concat(Enumerable.Repeat("PVT ", 100)));

        Assert.True(result.NameNormalized.Length <= CompanyNameNormalizer.MaxLength);
        Assert.True(result.NameCore.Length <= CompanyNameNormalizer.MaxLength);
    }

    [Fact]
    public void Idempotent()
    {
        foreach (var name in new[] { "M/s. ABC (P) Ltd", "Sunrise Advisors L.L.P.", "Gupta & Co. Ltd", "The Indian Hotels Company Limited" })
        {
            var once = CompanyNameNormalizer.Normalize(name).NameNormalized;
            Assert.Equal(once, CompanyNameNormalizer.Normalize(once).NameNormalized);
        }
    }
}
