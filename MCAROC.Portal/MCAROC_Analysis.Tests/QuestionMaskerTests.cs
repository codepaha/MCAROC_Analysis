using MCAROC_Analysis.Services.Chat;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499: a question kept in the cross-client repository carries nothing that identifies the client.</summary>
public class QuestionMaskerTests
{
    private static readonly MaskContext Coastal = new(["Coastal Projects Limited", "Coastal Projects Private Limited"], ["Sabbineni Surendra"]);

    [Theory]
    [InlineData("When did Coastal Projects Limited change its name?", "When did [company] change its name?")]
    [InlineData("when did coastal projects change its name", "when did [company] change its name")]   // without the legal form, any case
    [InlineData("Is Sabbineni Surendra still a director?", "Is [person] still a director?")]
    [InlineData("Show the MGT-7 of U45200TG1995PLC020034", "Show the MGT-7 of [cin]")]
    [InlineData("Which charges are registered against PAN AABCC1234D?", "Which charges are registered against PAN [pan]?")]
    [InlineData("What is the status of OA/77/2022 at DRT Hyderabad?", "What is the status of [case-no] at DRT Hyderabad?")]
    [InlineData("Is the Rs 2.5 crore loan from SBI satisfied?", "Is the [amount] loan from SBI satisfied?")]
    [InlineData("Who is the contact at 9876543210 or cfo@example.com?", "Who is the contact at [contact] or [contact]?")]
    [InlineData("Find SRN 123456789 filing", "Find SRN [number] filing")]
    public void Identifying_details_become_placeholders(string question, string expected) =>
        Assert.Equal(expected, QuestionMasker.Mask(question, Coastal));

    [Fact]
    public void Years_lenders_and_form_names_stay_they_say_what_kind_of_question_it_is()
    {
        const string q = "Did the company file AOC-4 for FY 2023 and what did HDFC Bank lend in 2021?";
        Assert.Equal(q, QuestionMasker.Mask(q, Coastal));
    }

    [Fact]
    public void A_one_word_core_name_is_masked_only_in_full()
    {
        // "Lodha Limited" → "Lodha" alone is too common a word to strip everywhere; the full name is masked.
        var ctx = new MaskContext(["Lodha Limited"], []);
        Assert.Equal("Charges of [company]", QuestionMasker.Mask("Charges of Lodha Limited", ctx));
    }
}
