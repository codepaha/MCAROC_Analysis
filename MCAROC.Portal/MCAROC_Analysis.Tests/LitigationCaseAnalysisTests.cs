using System.Text.Json.Nodes;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>A scripted model answer for a v2.0 case prompt, built from the case JSON the prompt carries: valid, claiming nothing the
/// text cannot back (no quotes, tier R0 so the baseline stands).</summary>
internal static class CaseAnalysisAnswers
{
    public static string For(string prompt, Action<JsonObject>? tweak = null)
    {
        var json = prompt[(prompt.IndexOf("CASE:\n", StringComparison.Ordinal) + "CASE:\n".Length)..];
        var c = (JsonObject)JsonNode.Parse(json)!;
        var orders = (JsonArray)c["orders"]!;
        var withText = orders.OfType<JsonObject>().Any(o => !string.IsNullOrWhiteSpace(o["order_text"]?.ToString()));
        var answer = new JsonObject
        {
            ["case_key"] = c["case_key"]!.ToString(), ["analysis_basis"] = withText ? "Order-Confirmed" : "Metadata-Led",
            ["target_party_role"] = "Respondent", ["legal_exposure_direction"] = "AGAINST_TARGET",
            ["risk"] = "R0", ["risk_trigger"] = "Scripted", ["risk_reason"] = "Scripted answer.", ["risk_category"] = null,
            ["relevance"] = "Monitor", ["case_nature"] = "Civil", ["liability_type"] = "Civil", ["summary"] = "Scripted case summary.",
            ["key_points"] = new JsonArray("Scripted point one.", "Scripted point two."),
            ["subject"] = "Civil - Test", ["amount"] = null, ["factual_background"] = null, ["relief_sought"] = null, ["core_issue"] = null,
            ["judicial_reasoning"] = new JsonArray(), ["provisions"] = new JsonArray(), ["precedents"] = new JsonArray(),
            ["connected_matters"] = new JsonArray(), ["active_restraint"] = false, ["restraint_type"] = "NONE", ["lis_pendens"] = false,
            ["order_analysis"] = new JsonArray(orders.OfType<JsonObject>().Select(o => (JsonNode)new JsonObject
                { ["order_id"] = (long)o["order_id"]!, ["date"] = o["order_date"]?.ToString(), ["type"] = null, ["summary"] = "Scripted.", ["key_findings"] = new JsonArray(), ["operative_order"] = null }).ToArray()),
            ["outcome"] = null, ["consequence_note"] = null, ["evidence"] = new JsonArray(), ["unknowns"] = new JsonArray()
        };
        tweak?.Invoke(answer);
        return answer.ToJsonString();
    }
}

public class LitigationCaseAnalysisTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 3);
    private const string Target = "Coastal Projects Limited";

    private const string OrderOneText =
        "--- Page 1 (native) ---\nBefore the Debts Recovery Tribunal.\nThe application is allowed. The respondent shall not alienate the mortgaged property at Survey No. 12/3, Village Baner.\n" +
        "--- Page 2 (native) ---\nIN THE RESULT the application is allowed and the respondent is restrained from creating any third party interest.";

    private static LitigationCase NewCase(params (long Id, string Date, string Type)[] orders) => new()
    {
        LitigationCaseId = 7, Type = "drt", Court = "Debts Recovery Tribunal", CourtCategory = "high_risk_court", CaseNumber = "OA/12/2021",
        CaseStatus = "PENDING", Direction = "against", PetitionersJson = "[\"Karur Vysya Bank Limited\"]", RespondentsJson = "[\"Coastal Projects Limited\"]",
        Orders = orders.Select(o => new LitigationCaseOrder { LitigationCaseOrderId = o.Id, OrderDate = o.Date, OrderType = o.Type }).ToList()
    };

    private static Dictionary<long, LitigationOrderDocument> Docs(params (long OrderId, long DocId, string? Text)[] docs) =>
        docs.ToDictionary(d => d.OrderId, d => new LitigationOrderDocument { LitigationCaseOrderId = d.OrderId, LitigationOrderDocumentId = d.DocId, ExtractedText = d.Text });

    private static CaseAnalysisInput Input(LitigationCase c, Dictionary<long, LitigationOrderDocument> docs) =>
        LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target, "COASTAL PROJECTS PRIVATE LIMITED"], AsOf);

    private static string Answer(CaseAnalysisInput input, Action<JsonObject>? tweak = null) =>
        CaseAnalysisAnswers.For(LitigationCaseAnalysisPrompt.Build(input.CaseJson), tweak);

    // ── Input ──

    [Fact]
    public void The_input_carries_every_order_with_its_full_text_newest_first_and_the_target()
    {
        var c = NewCase((1, "26-04-2021", "Order"), (2, "09-01-2025", "Final Order"));
        var input = Input(c, Docs((1, 11, "old text"), (2, 12, OrderOneText)));

        var json = JsonNode.Parse(input.CaseJson)!;
        Assert.Equal("7", json["case_key"]!.ToString());
        Assert.Equal("drt", json["court_type"]!.ToString());
        Assert.Equal("against", json["party_direction"]!.ToString());
        Assert.Equal(Target, json["target_entity"]!.ToString());
        Assert.Equal("2026-10-03", json["as_of_date"]!.ToString());
        var orders = (JsonArray)json["orders"]!;
        Assert.Equal([2L, 1L], orders.Select(o => (long)o!["order_id"]!)); // by date, not by text
        Assert.Equal(OrderOneText, orders[0]!["order_text"]!.ToString());
        Assert.False((bool)json["text_truncated"]!);
        Assert.True(input.HasText);
    }

    [Fact]
    public void The_stored_evidence_holds_hashes_not_text_and_does_not_change_from_one_day_to_the_next()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        var docs = Docs((1, 11, OrderOneText));

        var today = LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target], AsOf);
        var tomorrow = LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target], AsOf.AddDays(1));

        Assert.DoesNotContain("Survey No. 12/3", today.EvidenceJson);
        Assert.Contains("text_sha256", today.EvidenceJson);
        Assert.Equal(today.EvidenceHash, tomorrow.EvidenceHash);
        var changed = LitigationCaseAnalysisInputBuilder.Build(c, Docs((1, 11, OrderOneText + " More.")), Target, [Target], AsOf);
        Assert.NotEqual(today.EvidenceHash, changed.EvidenceHash); // any change to an order's text is a change to the evidence
    }

    [Fact]
    public void A_very_long_order_is_cut_to_its_head_and_tail_and_flagged()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        var text = new string('a', 100_000) + " OPERATIVE END";
        var input = Input(c, Docs((1, 11, text)));

        var sent = input.Orders.Single().Text!;
        Assert.True(input.TextTruncated);
        Assert.Contains(LitigationCaseAnalysisInputBuilder.OmittedMarker, sent);
        Assert.EndsWith("OPERATIVE END", sent);
        Assert.True(sent.Length <= LitigationCaseAnalysisInputBuilder.MaxOrderChars + 100);
        Assert.Equal(text.Length, input.Orders.Single().TotalChars);
    }

    [Fact]
    public void Past_the_case_budget_the_oldest_orders_lose_their_text_not_the_newest()
    {
        var orders = Enumerable.Range(1, 20).Select(i => ((long)i, $"{i:00}-01-2025", "Order")).ToArray();   // 20 x 55k > the 1M case limit
        var docs = Docs(orders.Select(o => (o.Item1, o.Item1 + 100, (string?)new string('x', 55_000))).ToArray());
        var input = Input(NewCase(orders), docs);

        Assert.True(input.TextTruncated);
        Assert.NotNull(input.Orders.First(o => o.OrderId == 20).Text);  // newest keeps its text
        Assert.Null(input.Orders.First(o => o.OrderId == 1).Text);      // oldest was left out
    }

    [Fact]
    public void A_case_with_no_readable_text_is_metadata_led()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs());
        Assert.False(input.HasText);
        Assert.Contains("\"order_text\":null", input.CaseJson);
    }

    // ── Validation ──

    private static CaseAnalysisValidation Validate(string answer, CaseAnalysisInput input, LitigationRiskTier baseline = LitigationRiskTier.R3) =>
        LitigationCaseAnalysisValidator.Validate(answer, input, baseline, "DRT Recovery Proceeding");

    [Fact]
    public void A_valid_answer_is_accepted_and_keeps_the_baseline_tier_when_the_model_says_lower()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var v = Validate(Answer(input), input);

        Assert.True(v.IsAccepted, v.RejectReason);
        var json = JsonNode.Parse(v.AnalysisJson!)!;
        Assert.Equal("R3", json["risk"]!.ToString());          // the model said R0; the record says R3
        Assert.Equal("DRT Recovery Proceeding", json["risk_trigger"]!.ToString());
        Assert.Equal("Needs attention", json["relevance"]!.ToString());
        Assert.Equal("Completed", json["status"]!.ToString());
        Assert.Equal("Scripted case summary.", json["summary"]!.ToString());
        Assert.Contains(v.Notes, n => n.Contains("below the record-based baseline"));
    }

    [Fact]
    public void A_raised_tier_stands_only_on_a_quote_that_is_really_in_the_order()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));
        string Raise(string quote) => Answer(input, a =>
        {
            a["risk"] = "R3"; a["risk_trigger"] = "Active Restraint";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = quote });
        });

        var grounded = Validate(Raise("restrained from creating any third party interest"), input, LitigationRiskTier.R1);
        Assert.Equal("R3", JsonNode.Parse(grounded.AnalysisJson!)!["risk"]!.ToString());
        Assert.True((bool)JsonNode.Parse(grounded.AnalysisJson!)!["risk_raised"]!);
        var evidence = Assert.Single((JsonArray)JsonNode.Parse(grounded.AnalysisJson!)!["evidence"]!)!;
        Assert.Equal(2, (int)evidence["page"]!);           // page worked out from the markers
        Assert.Equal(11, (long)evidence["document_id"]!);

        var invented = Validate(Raise("the court granted an ex parte injunction against the company"), input, LitigationRiskTier.R1);
        var json = JsonNode.Parse(invented.AnalysisJson!)!;
        Assert.Equal("R1", json["risk"]!.ToString());      // no confirmed quote: the baseline stays
        Assert.Contains(invented.Notes, n => n.Contains("not a verbatim quote"));
        Assert.Contains(invented.Notes, n => n.Contains("without a confirmed quote"));
    }

    [Fact]
    public void A_restraint_or_lis_pendens_flag_without_a_confirmed_quote_goes_back_to_false()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var unbacked = Validate(Answer(input, a => { a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION"; a["lis_pendens"] = true; }), input);
        var json = JsonNode.Parse(unbacked.AnalysisJson!)!;
        Assert.False((bool)json["active_restraint"]!);
        Assert.False((bool)json["lis_pendens"]!);
        Assert.Equal("NONE", json["restraint_type"]!.ToString());

        var backed = Validate(Answer(input, a =>
        {
            a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint", ["order_id"] = 1, ["quote"] = "shall not alienate the mortgaged property at Survey No. 12/3, Village Baner" });
        }), input);
        var kept = JsonNode.Parse(backed.AnalysisJson!)!;
        Assert.True((bool)kept["active_restraint"]!);
        Assert.Equal("INJUNCTION", kept["restraint_type"]!.ToString());
    }

    [Fact]
    public void A_quote_that_cuts_an_identifier_is_not_confirmed()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var v = Validate(Answer(input, a =>
        {
            a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint", ["order_id"] = 1, ["quote"] = "shall not alienate the mortgaged property at Survey No. 12" });
        }), input);

        Assert.False((bool)JsonNode.Parse(v.AnalysisJson!)!["active_restraint"]!);
    }

    [Fact]
    public void A_quote_naming_an_order_without_text_or_an_unknown_order_is_dropped()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order"), (2, "01-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var v = Validate(Answer(input, a =>
        {
            a["evidence"] = new JsonArray(
                new JsonObject { ["claim"] = "operative_order", ["order_id"] = 2, ["quote"] = "The application is allowed." },
                new JsonObject { ["claim"] = "operative_order", ["order_id"] = 99, ["quote"] = "The application is allowed." },
                new JsonObject { ["claim"] = "operative_order", ["order_id"] = 1, ["quote"] = "The application is allowed." });
        }), input);

        var evidence = (JsonArray)JsonNode.Parse(v.AnalysisJson!)!["evidence"]!;
        Assert.Single(evidence);
        Assert.Equal(1, (long)evidence[0]!["order_id"]!);
    }

    [Fact]
    public void An_answer_for_another_case_or_with_a_broken_shape_is_rejected()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        Assert.False(Validate(Answer(input, a => a["case_key"] = "8"), input).IsAccepted);
        Assert.False(Validate(Answer(input, a => a["risk"] = "R9"), input).IsAccepted);
        Assert.False(Validate(Answer(input, a => a["summary"] = ""), input).IsAccepted);
        Assert.False(Validate("not json", input).IsAccepted);
        Assert.False(Validate("[]", input).IsAccepted);
    }

    [Fact]
    public void The_basis_is_set_by_the_text_that_was_sent_not_by_the_models_claim()
    {
        var noText = Input(NewCase((1, "09-01-2025", "Order")), Docs());

        var v = Validate(Answer(noText, a => a["analysis_basis"] = "Order-Confirmed"), noText);

        Assert.Equal("Metadata-Led", JsonNode.Parse(v.AnalysisJson!)!["analysis_basis"]!.ToString());
        Assert.Contains(v.Notes, n => n.Contains("analysis_basis corrected"));
    }

    [Fact]
    public void Order_entries_for_orders_not_in_the_case_are_dropped_and_the_old_reference_shape_is_kept_for_readers()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var v = Validate(Answer(input, a =>
        {
            ((JsonArray)a["order_analysis"]!).Add(new JsonObject { ["order_id"] = 55, ["summary"] = "ghost" });
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "operative_order", ["order_id"] = 1, ["quote"] = "IN THE RESULT the application is allowed" });
        }), input);

        var json = JsonNode.Parse(v.AnalysisJson!)!;
        Assert.Single((JsonArray)json["order_analysis"]!);
        var reference = Assert.Single((JsonArray)json["evidenceReferences"]!)!;
        Assert.Equal(1, (long)reference["litigationCaseOrderId"]!);
        Assert.Equal(2, (int)reference["pageNumber"]!);
    }

    [Fact]
    public void The_stored_analysis_is_read_back_for_the_page_with_its_quotes_and_order_findings()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));
        var v = Validate(Answer(input, a =>
        {
            a["summary"] = "A DRT application against the company.";
            a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
            a["provisions"] = new JsonArray("Section 19, Recovery of Debts and Bankruptcy Act 1993");
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint", ["order_id"] = 1, ["quote"] = "restrained from creating any third party interest" });
            ((JsonArray)a["order_analysis"]!)[0]!["summary"] = "The tribunal allowed the application.";
        }), input);

        var vm = MCAROC_Analysis.Services.LitigationData.LitigationCaseCardMapper.MapAnalysis(
            new LitigationCaseAiAnalysis { LitigationCaseAiAnalysisId = 3, Status = LitigationAiAnalysisItemStatus.Completed, AnalysisJson = v.AnalysisJson }, 2);

        Assert.True(vm.IsDetailed);
        Assert.Equal("R3", vm.RiskLevel);
        Assert.Equal("R3", vm.BaselineRisk);
        Assert.True(vm.ActiveRestraint);
        Assert.Equal("INJUNCTION", vm.RestraintType);
        Assert.Equal("A DRT application against the company.", vm.Summary);
        Assert.Equal(["Section 19, Recovery of Debts and Bankruptcy Act 1993"], vm.Provisions);
        var finding = Assert.Single(vm.OrderFindings);
        Assert.Equal((1L, 11L), (finding.OrderId, finding.DocumentId!.Value));
        Assert.Equal("The tribunal allowed the application.", finding.Summary);
        var quote = Assert.Single(vm.Quotes);
        Assert.Equal(("restraint", 11L, 2), (quote.Claim, quote.DocumentId!.Value, quote.Page));
        Assert.Single(vm.Citations); // the older reference shape is still read
    }

    [Fact]
    public void The_portfolio_sees_the_conclusions_of_a_case_not_its_quotes_and_order_findings()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));
        var v = Validate(Answer(input), input);

        var compact = JsonNode.Parse(LitigationCaseAnalysisProjection.ForPortfolio(v.AnalysisJson)!)!;

        Assert.Equal("R3", compact["risk"]!.ToString());
        Assert.NotNull(compact["summary"]);
        Assert.Null(compact["order_analysis"]);
        Assert.Null(compact["evidence"]);
        Assert.True(compact.ToJsonString().Length < v.AnalysisJson!.Length);
        // An analysis in the older shape, or none, passes through untouched.
        Assert.Equal("{\"summary\":\"ok\"}", LitigationCaseAnalysisProjection.ForPortfolio("{\"summary\":\"ok\"}"));
        Assert.Null(LitigationCaseAnalysisProjection.ForPortfolio(null));
    }

    [Fact]
    public void A_quote_that_differs_from_the_extracted_text_only_in_spacing_is_confirmed_as_the_text_has_it()
    {
        // Extracted court-order text often runs words together or splits them; the model writes them with normal spacing.
        const string messy = "--- Page 1 (native) ---\nIN THE DEBTS RECOVERYTRIBUNAL. The Application is filed u/s.19 of the Recovery of Debts& Bankruptcy Act, 1993 and OCTOBE R, 2023.";
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, messy)));

        var v = Validate(Answer(input, a => a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "operative_order", ["order_id"] = 1, ["quote"] = "filed u/s.19 of the Recovery of Debts & Bankruptcy Act, 1993" })), input);

        var evidence = Assert.Single((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["evidence"]!)!;
        Assert.Equal("filed u/s.19 of the Recovery of Debts& Bankruptcy Act, 1993", evidence["quote"]!.ToString()); // stored as the order prints it
        Assert.Equal(1, (int)evidence["page"]!);
    }

    [Fact]
    public void Ignoring_spacing_does_not_let_a_quote_cut_an_identifier()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));

        var v = Validate(Answer(input, a =>
        {
            a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint", ["order_id"] = 1, ["quote"] = "shallnot alienate the mortgaged property at Survey No. 12" });
        }), input);

        Assert.False((bool)JsonNode.Parse(v.AnalysisJson!)!["active_restraint"]!);
    }

    [Fact]
    public void An_amount_not_printed_in_any_order_is_dropped_and_a_printed_one_is_kept_whatever_its_commas()
    {
        const string text = "--- Page 1 (native) ---" + "\n" + "The respondents shall pay a total sum of Rs.52,51,19,676.60 with interest.";
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, text)));

        var printed = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = "Rs. 5,251,19,676.60"), input).AnalysisJson!)!;
        Assert.Equal("Rs. 5,251,19,676.60", printed["amount"]!.ToString());
        Assert.Empty((JsonArray)printed["unknowns"]!);

        var invented = Validate(Answer(input, a => a["amount"] = "Rs. 9,999,99,999"), input);
        var json = JsonNode.Parse(invented.AnalysisJson!)!;
        Assert.Null(json["amount"]);
        Assert.Contains(((JsonArray)json["unknowns"]!).Select(x => x!.ToString()), u => u.Contains("could not be matched"));
    }

    [Fact]
    public void A_large_figure_in_the_prose_that_is_not_printed_is_flagged_not_silently_trusted()
    {
        const string text = "--- Page 1 (native) ---" + "\n" + "The respondents shall pay a total sum of Rs.52,51,19,676.60 with interest.";
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, text)));

        var v = Validate(Answer(input, a => a["summary"] = "The bank recovered over Rs. 7777 crore; the case was filed in 2018."), input);

        var unknowns = ((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["unknowns"]!).Select(x => x!.ToString()).ToList();
        Assert.Contains(unknowns, u => u.Contains("7777") && u.Contains("not confirmed"));
        Assert.DoesNotContain(unknowns, u => u.Contains("2018")); // a year is not an amount
    }

    [Theory]
    [InlineData("The property at Survey No. 12 / 3 is attached.", "The property at Survey No. 12")]      // spaced slash
    [InlineData("The property at Survey No. 12/ 3 is attached.", "The property at Survey No. 12/")]      // joiner ending
    [InlineData("The flat is Flat No. 305 - A in Sunrise.", "The flat is Flat No. 305")]                 // letter-number unit
    [InlineData("The unit is Unit No. A - 305 in Tower B.", "305 in Tower B")]                           // starts after the letter
    public void The_spacing_fallback_applies_the_same_identifier_rules_as_the_exact_match(string source, string cut)
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + source)));

        var v = Validate(Answer(input, a =>
        {
            a["risk"] = "R3"; a["risk_trigger"] = "Raised";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = cut });
        }), input, LitigationRiskTier.R0);

        var json = JsonNode.Parse(v.AnalysisJson!)!;
        Assert.Equal("R0", json["risk"]!.ToString());          // no valid quote: the baseline stays
        Assert.Empty((JsonArray)json["evidence"]!);
    }

    [Fact]
    public void Spacing_only_differences_still_match_a_whole_identifier()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "The property at Survey No. 12 / 3, Village Baner is attached.")));

        var v = Validate(Answer(input, a =>
        {
            a["risk"] = "R3"; a["risk_trigger"] = "Raised";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = "The property at Survey No. 12/3, Village Baner" });
        }), input, LitigationRiskTier.R0);

        Assert.Equal("R3", JsonNode.Parse(v.AnalysisJson!)!["risk"]!.ToString());
    }

    [Theory]
    [InlineData("Rs. 1234500", "Rs. 12345")]            // a prefix of a different number
    [InlineData("Rs. 12345.50", "Rs. 12345")]           // the whole part of a decimal amount
    [InlineData("Rs. 912345", "Rs. 12345")]             // a suffix of a different number
    public void An_amount_that_is_only_part_of_a_printed_number_is_dropped(string printed, string reported)
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "The respondent shall pay " + printed + " within four weeks.")));

        var json = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = reported), input).AnalysisJson!)!;

        Assert.Null(json["amount"]);
        Assert.Contains(((JsonArray)json["unknowns"]!).Select(x => x!.ToString()), u => u.Contains("could not be matched"));
    }

    [Theory]
    [InlineData("Rs. 12,345", "Rs. 12345")]
    [InlineData("Rs.12345.50 only", "Rs. 12,345.50")]
    [InlineData("a sum of Rs. 5,00,00,000/-", "Rs. 5,00,00,000")]
    public void A_printed_amount_is_kept_whatever_its_comma_grouping(string printed, string reported)
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "The respondent shall pay " + printed + " within four weeks.")));

        var json = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = reported), input).AnalysisJson!)!;

        Assert.Equal(reported, json["amount"]!.ToString());
    }

    [Fact]
    public void A_figure_is_not_matched_across_two_orders()
    {
        // "12" ends order 1 and "345" starts order 2: together they are not the printed number 12345.
        var input = Input(NewCase((1, "09-01-2025", "Order"), (2, "08-01-2025", "Order")),
            Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "Costs of Rs. 12"), (2, 12, "--- Page 1 (native) ---" + "\n" + "345 was the earlier figure.")));

        var json = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = "Rs. 12345"), input).AnalysisJson!)!;

        Assert.Null(json["amount"]);
    }

    [Fact]
    public void A_quote_in_the_tail_of_a_cut_order_is_linked_to_the_page_it_is_really_on()
    {
        // Page 1 marker + 30,000 chars; page 2 marker + 70,000 chars + the operative sentence. The page 2 marker falls in the omitted middle.
        var text = "--- Page 1 (native) ---" + "\n" + new string('a', 30_000) + "\n--- Page 2 (native) ---" + "\n" + new string('b', 70_000) + " The application is allowed with costs.";
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, text)));
        Assert.True(input.TextTruncated);

        var v = Validate(Answer(input, a => a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "operative_order", ["order_id"] = 1, ["quote"] = "The application is allowed with costs." })), input);

        var evidence = Assert.Single((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["evidence"]!)!;
        Assert.Equal(2, (int)evidence["page"]!);
    }

    [Theory]
    [InlineData("Rs. 2000")]     // year-shaped
    [InlineData("Rs. 999")]      // under four digits
    public void A_small_or_year_shaped_amount_must_be_printed_too(string reported)
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "The application is dismissed with no order as to costs.")));

        var json = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = reported), input).AnalysisJson!)!;

        Assert.Null(json["amount"]);
    }

    [Theory]
    [InlineData("Rs. 2000", "Rs. 2000")]
    [InlineData("Rs. 999 as costs", "Rs. 999")]
    public void A_small_or_year_shaped_amount_that_is_printed_is_kept(string printed, string reported)
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, "--- Page 1 (native) ---" + "\n" + "Costs of " + printed + " are awarded.")));

        var json = JsonNode.Parse(Validate(Answer(input, a => a["amount"] = reported), input).AnalysisJson!)!;

        Assert.Equal(reported, json["amount"]!.ToString());
    }

    [Fact]
    public void A_tail_quote_gets_its_real_page_from_windows_line_endings_too()
    {
        var text = ("--- Page 1 (native) ---" + "\n" + new string('a', 30_000) + "\n--- Page 2 (native) ---" + "\n" + new string('b', 70_000) + " The application is allowed with costs.").Replace("\n", "\r\n");
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, text)));

        var v = Validate(Answer(input, a => a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "operative_order", ["order_id"] = 1, ["quote"] = "The application is allowed with costs." })), input);

        var evidence = Assert.Single((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["evidence"]!)!;
        Assert.Equal(2, (int)evidence["page"]!);
    }

    // ── Long cases: grouped calls ──

    private static CaseAnalysisInput LongInput(params int[] sizes)
    {
        var orders = sizes.Select((n, i) => ((long)(i + 1), $"{(i + 1):00}-01-2025", "Order")).ToArray();
        var docs = Docs(sizes.Select((n, i) => ((long)(i + 1), (long)(i + 101), (string?)(n == 0 ? null : new string('x', n)))).ToArray());
        return Input(NewCase(orders), docs);
    }

    [Fact]
    public void A_case_within_the_limit_is_one_group()
    {
        var plan = LitigationCaseAnalysisGroups.Plan(LongInput(30_000, 30_000, 30_000), splitAbove: 100_000, groupChars: 80_000);

        Assert.Single(plan);
        Assert.Equal(3, plan[0].Count);
    }

    [Fact]
    public void A_long_case_is_cut_into_groups_of_orders_newest_first_with_no_order_in_two_groups()
    {
        // orders 1..5 (order 5 is the newest): 50k, 50k, 50k, 50k, 0 (no text)
        var plan = LitigationCaseAnalysisGroups.Plan(LongInput(50_000, 50_000, 50_000, 50_000, 0), splitAbove: 100_000, groupChars: 80_000);

        Assert.Equal(4, plan.Count);                                     // 50k fits one per group at an 80k limit
        Assert.True(plan[0].SetEquals([5L, 4L]));                        // the order without text rides with the newest group
        Assert.Equal(5, plan.SelectMany(g => g).Distinct().Count());     // every order exactly once
        Assert.Equal(5, plan.Sum(g => g.Count));
    }

    [Fact]
    public void An_input_built_for_one_group_holds_only_its_orders_and_is_checked_against_only_their_text()
    {
        var whole = LongInput(50_000, 50_000, 10_000);
        var c = NewCase((1, "01-01-2025", "Order"), (2, "02-01-2025", "Order"), (3, "03-01-2025", "Order"));
        var docs = Docs((1, 101, new string('x', 50_000)), (2, 102, new string('y', 50_000)), (3, 103, "--- Page 1 (native) ---" + "\n" + "The application is allowed with costs."));

        var part = LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target], AsOf, new HashSet<long> { 3 });

        Assert.Equal([3L], part.Orders.Select(o => o.OrderId));
        Assert.Equal(3, whole.Orders.Count);
        // A quote naming order 2 is not in this group's input, so it is dropped.
        var v = Validate(Answer(part, a => a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "operative_order", ["order_id"] = 2, ["quote"] = "yyyyyyyyyyyyyyyy" },
            new JsonObject { ["claim"] = "operative_order", ["order_id"] = 3, ["quote"] = "The application is allowed with costs." })), part);
        Assert.Equal(3L, (long)Assert.Single((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["evidence"]!)!["order_id"]!);
    }

    [Fact]
    public void Merged_group_results_take_the_highest_tier_any_flag_and_every_finding()
    {
        var newest = Answer(Input(NewCase((3, "03-01-2025", "Order")), Docs((3, 103, "--- Page 1 (native) ---" + "\n" + "Order three text."))),
            a => { a["summary"] = "Newest orders: hearing adjourned."; a["provisions"] = new JsonArray("Section 19, RDB Act"); });
        var older = Answer(Input(NewCase((1, "01-01-2025", "Order")), Docs((1, 101, "--- Page 1 (native) ---" + "\n" + "Order one text."))),
            a => { a["risk"] = "R3"; a["risk_trigger"] = "Active Restraint"; a["summary"] = "Earlier orders: a restraint was granted."; a["provisions"] = new JsonArray("Section 19, RDB Act", "Section 22, RDB Act"); a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = "Order one text." }); });
        string Ok(string answer, long order) => LitigationCaseAnalysisValidator.Validate(answer, order == 3
            ? Input(NewCase((3, "03-01-2025", "Order")), Docs((3, 103, "--- Page 1 (native) ---" + "\n" + "Order three text.")))
            : Input(NewCase((1, "01-01-2025", "Order")), Docs((1, 101, "--- Page 1 (native) ---" + "\n" + "Order one text."))),
            LitigationRiskTier.R0, "Baseline").AnalysisJson!;

        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([Ok(newest, 3), Ok(older, 1)]))!;

        Assert.Equal("R3", merged["risk"]!.ToString());
        Assert.Equal("Active Restraint", merged["risk_trigger"]!.ToString());
        Assert.Equal(2, ((JsonArray)merged["provisions"]!).Count);                       // joined, no repeat
        Assert.StartsWith("Earlier orders", merged["summary"]!.ToString());             // the headline is the highest-tier group's, not a story of both
        Assert.Equal(2, (int)merged["split_groups"]!);
        Assert.Contains(((JsonArray)merged["unknowns"]!).Select(x => x!.ToString()), u => u.Contains("2 groups"));
        Assert.Equal(2, ((JsonArray)merged["order_analysis"]!).Count);
    }

    [Fact]
    public void Key_points_are_kept_as_short_bullets_trimmed_deduplicated_and_capped_at_six()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));
        var long300 = new string('w', 400);

        var v = Validate(Answer(input, a => a["key_points"] = new JsonArray(
            "- Claim: Rs.52 crore by 13 banks", "  * Company is 1st respondent  ", "Claim: Rs.52 crore by 13 banks", "", "Disposed 2023", "Allowed with costs", "p5", "p6", "p7", long300)), input);

        var points = ((JsonArray)JsonNode.Parse(v.AnalysisJson!)!["key_points"]!).Select(x => x!.ToString()).ToList();
        Assert.Equal(6, points.Count);
        Assert.Equal("Claim: Rs.52 crore by 13 banks", points[0]);   // list marker stripped, the repeat dropped
        Assert.Equal("Company is 1st respondent", points[1]);
        Assert.DoesNotContain(points, p => p.Length > 300);
    }

    [Fact]
    public void The_prompt_asks_for_a_headline_bullets_and_short_fields()
    {
        var prompt = LitigationCaseAnalysisPrompt.Instructions;

        Assert.Contains("key_points: 3 to 6 bullets", prompt);
        Assert.Contains("ONE line, at most 25 words", prompt);
        Assert.Contains("\"key_points\":[]", prompt);
        Assert.DoesNotContain("2-3 sentences", prompt);
        Assert.Equal("2.2", LitigationCaseAnalysisPrompt.Version);
    }

    [Fact]
    public void The_stored_key_points_are_read_back_and_a_merged_long_case_keeps_the_top_headline_and_all_bullets()
    {
        var input = Input(NewCase((1, "09-01-2025", "Order")), Docs((1, 11, OrderOneText)));
        var first = Validate(Answer(input, a => { a["summary"] = "Low group headline."; a["key_points"] = new JsonArray("Point A", "Shared point"); }), input, LitigationRiskTier.R0).AnalysisJson!;
        var second = Validate(Answer(input, a =>
        {
            a["summary"] = "High group headline."; a["key_points"] = new JsonArray("Shared point", "Point B");
            a["risk"] = "R3"; a["risk_trigger"] = "Active Restraint";
            a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = "restrained from creating any third party interest" });
        }), input, LitigationRiskTier.R0).AnalysisJson!;

        var merged = LitigationCaseAnalysisGroups.Merge([first, second]);

        var json = JsonNode.Parse(merged)!;
        Assert.Equal("High group headline.", json["summary"]!.ToString());
        Assert.Equal(["Point A", "Shared point", "Point B"], ((JsonArray)json["key_points"]!).Select(x => x!.ToString()));
        var vm = MCAROC_Analysis.Services.LitigationData.LitigationCaseCardMapper.MapAnalysis(
            new LitigationCaseAiAnalysis { Status = LitigationAiAnalysisItemStatus.Completed, AnalysisJson = merged }, 1);
        Assert.Equal(["Point A", "Shared point", "Point B"], vm.KeyPoints);
    }

    // ── #392 review: restraint chronology, evidence covers what groups send, group budgets ──

    private const string GrantText = "--- Page 1 (native) ---" + "\n" + "The respondent is restrained from alienating the property until further orders.";
    private const string LiftText = "--- Page 1 (native) ---" + "\n" + "The earlier injunction is vacated and no restraint remains in force.";
    private const string QuietText = "--- Page 1 (native) ---" + "\n" + "The matter is adjourned to the next date for arguments.";

    private static string GroupAnswer(string text, long orderId, Action<JsonObject>? tweak) =>
        LitigationCaseAnalysisValidator.Validate(
            CaseAnalysisAnswers.For(LitigationCaseAnalysisPrompt.Build(Input(NewCase((orderId, "01-01-2025", "Order")), Docs((orderId, orderId + 100, text))).CaseJson), tweak),
            Input(NewCase((orderId, "01-01-2025", "Order")), Docs((orderId, orderId + 100, text))), LitigationRiskTier.R0, "Baseline").AnalysisJson!;

    private static string Granted(long id) => GroupAnswer(GrantText, id, a =>
    {
        a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
        a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint", ["order_id"] = id, ["quote"] = "The respondent is restrained from alienating the property" });
    });
    private static string Lifted(long id) => GroupAnswer(LiftText, id, a =>
    {
        a["restraint_lifted"] = true;
        a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint_lifted", ["order_id"] = id, ["quote"] = "The earlier injunction is vacated" });
    });
    private static string Quiet(long id) => GroupAnswer(QuietText, id, null);

    [Fact]
    public void A_restraint_lifted_by_a_newer_group_is_not_in_force_in_the_merged_case()
    {
        // groups are passed newest first: order 2 (newer) lifts what order 1 (older) granted
        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([Lifted(2), Granted(1)]))!;

        Assert.False((bool)merged["active_restraint"]!);
        Assert.Equal("NONE", merged["restraint_type"]!.ToString());
    }

    [Fact]
    public void A_restraint_a_newer_group_never_mentions_stays_in_force_and_a_renewed_one_is_in_force()
    {
        var untouched = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([Quiet(2), Granted(1)]))!;
        Assert.True((bool)untouched["active_restraint"]!);
        Assert.Equal("INJUNCTION", untouched["restraint_type"]!.ToString());

        // granted, lifted, granted again: the newest grant stands
        var renewed = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([Granted(3), Lifted(2), Granted(1)]))!;
        Assert.True((bool)renewed["active_restraint"]!);
    }

    [Fact]
    public void A_lifted_flag_without_a_confirmed_quote_is_set_back_to_false()
    {
        var input = Input(NewCase((1, "01-01-2025", "Order")), Docs((1, 101, LiftText)));

        var v = Validate(Answer(input, a => a["restraint_lifted"] = true), input);

        Assert.False((bool)JsonNode.Parse(v.AnalysisJson!)!["restraint_lifted"]!);
    }

    [Fact]
    public void Text_beyond_the_old_case_budget_is_part_of_the_evidence_hash_and_groups_send_exactly_the_hashed_text()
    {
        var orders = Enumerable.Range(1, 9).Select(i => ((long)i, $"0{i}-01-2025", "Order")).ToArray();
        Dictionary<long, LitigationOrderDocument> DocsWith(char first) =>
            Docs(orders.Select(o => (o.Item1, o.Item1 + 100, (string?)new string(o.Item1 == 1 ? first : 'x', 55_000))).ToArray());
        var c = NewCase(orders);

        var a = LitigationCaseAnalysisInputBuilder.Build(c, DocsWith('x'), Target, [Target], AsOf);
        var b = LitigationCaseAnalysisInputBuilder.Build(c, DocsWith('y'), Target, [Target], AsOf);

        Assert.NotEqual(a.EvidenceHash, b.EvidenceHash);                     // a same-length change in the oldest order changes the hash
        // every group is built against the whole case and carries only text the whole case carries
        var plan = LitigationCaseAnalysisGroups.Plan(a, 100_000, 80_000);
        var sent = plan.SelectMany(ids => LitigationCaseAnalysisInputBuilder.Build(c, DocsWith('x'), Target, [Target], AsOf, ids,
            a.Orders.Where(o => o.Text is null && o.TotalChars > 0).Select(o => o.OrderId).ToHashSet()).Orders).ToList();
        Assert.Equal(a.Orders.Sum(o => o.Text?.Length ?? 0), sent.Sum(o => o.Text?.Length ?? 0));
    }

    [Fact]
    public void A_group_never_sends_more_text_than_the_group_budget_and_orders_over_the_case_limit_are_flagged_not_rehydrated()
    {
        var orders = Enumerable.Range(1, 20).Select(i => ((long)i, $"{i:00}-01-2025", "Order")).ToArray();   // 20 x 55k = 1.1M > 1M
        var docs = Docs(orders.Select(o => (o.Item1, o.Item1 + 100, (string?)new string('x', 55_000))).ToArray());
        var c = NewCase(orders);
        var whole = LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target], AsOf);
        Assert.True(whole.TextTruncated);
        var omitted = whole.Orders.Where(o => o.Text is null).Select(o => o.OrderId).ToHashSet();
        Assert.NotEmpty(omitted);

        var plan = LitigationCaseAnalysisGroups.Plan(whole, 100_000, 80_000);

        Assert.Equal(20, plan.SelectMany(g => g).Distinct().Count());       // every order exactly once
        foreach (var ids in plan)
        {
            var part = LitigationCaseAnalysisInputBuilder.Build(c, docs, Target, [Target], AsOf, ids, omitted);
            Assert.True(part.Orders.Sum(o => o.Text?.Length ?? 0) <= 80_000, "a group sent more than the group budget");
            Assert.All(part.Orders.Where(o => omitted.Contains(o.OrderId)), o => Assert.Null(o.Text));   // not rehydrated
        }
    }

    // ── #392 re-review: the tier follows the final restraint state ──

    private static string GroupWith(string text, long id, LitigationRiskTier baseline, Action<JsonObject> tweak)
    {
        var input = Input(NewCase((id, "01-01-2025", "Order")), Docs((id, id + 100, text)));
        return LitigationCaseAnalysisValidator.Validate(CaseAnalysisAnswers.For(LitigationCaseAnalysisPrompt.Build(input.CaseJson), tweak), input, baseline, "Baseline").AnalysisJson!;
    }

    /// <summary>An older group whose R3 rests on an injunction it grants; <paramref name="without"/> is the tier it would have had without it.</summary>
    private static string GrantedR3(long id, string without, string withoutTrigger = "Pending Civil Suit") => GroupWith(GrantText, id, LitigationRiskTier.R1, a =>
    {
        a["risk"] = "R3"; a["risk_trigger"] = "Active restraint"; a["risk_reason"] = "Injunction remains in force.";
        a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION";
        a["risk_without_restraint"] = without; a["risk_trigger_without_restraint"] = withoutTrigger; a["risk_reason_without_restraint"] = "Ordinary pending matter.";
        a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "restraint", ["order_id"] = id, ["quote"] = "The respondent is restrained from alienating the property" },
            new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = id, ["quote"] = "The respondent is restrained from alienating the property" });
    });

    private static string LiftedR1(long id) => GroupWith(LiftText, id, LitigationRiskTier.R1, a =>
    {
        a["risk"] = "R1"; a["risk_trigger"] = "Pending Civil Suit"; a["risk_reason"] = "Pending civil matter, restraint lifted.";
        a["restraint_lifted"] = true;
        a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "restraint_lifted", ["order_id"] = id, ["quote"] = "The earlier injunction is vacated" });
    });

    [Fact]
    public void A_tier_that_rested_only_on_a_restraint_that_a_newer_order_lifted_falls_with_it()
    {
        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([LiftedR1(2), GrantedR3(1, without: "R1")]))!;

        Assert.False((bool)merged["active_restraint"]!);
        Assert.Equal("R1", merged["risk"]!.ToString());
        Assert.Equal("Pending Civil Suit", merged["risk_trigger"]!.ToString());
        Assert.DoesNotContain("Injunction remains", merged["risk_reason"]!.ToString());
        Assert.Equal("Monitor", merged["relevance"]!.ToString());
    }

    [Fact]
    public void A_tier_with_an_independent_ground_keeps_it_when_the_restraint_is_lifted()
    {
        // the old group is R3 for a DRT recovery AND an injunction: without the injunction it is still R3
        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([LiftedR1(2), GrantedR3(1, without: "R3", withoutTrigger: "DRT Recovery Proceeding")]))!;

        Assert.False((bool)merged["active_restraint"]!);
        Assert.Equal("R3", merged["risk"]!.ToString());
        Assert.Equal("DRT Recovery Proceeding", merged["risk_trigger"]!.ToString());
        Assert.Equal("Needs attention", merged["relevance"]!.ToString());
    }

    [Fact]
    public void A_renewed_restraint_keeps_the_tier_it_gave()
    {
        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([GrantedR3(3, without: "R1"), LiftedR1(2), GrantedR3(1, without: "R1")]))!;

        Assert.True((bool)merged["active_restraint"]!);
        Assert.Equal("R3", merged["risk"]!.ToString());
        Assert.Equal("Active restraint", merged["risk_trigger"]!.ToString());
    }

    [Fact]
    public void The_without_restraint_tier_is_clamped_between_the_baseline_and_the_tier_given()
    {
        var above = JsonNode.Parse(GroupWith(GrantText, 1, LitigationRiskTier.R1, a => { a["risk"] = "R1"; a["risk_without_restraint"] = "R3"; }))!;
        Assert.Equal(above["risk"]!.ToString(), above["risk_without_restraint"]!.ToString());          // never above the tier given

        var below = JsonNode.Parse(GroupWith(GrantText, 1, LitigationRiskTier.R2, a => { a["risk"] = "R3"; a["risk_trigger"] = "x"; a["risk_without_restraint"] = "R0"; a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = 1, ["quote"] = "The respondent is restrained from alienating the property" }); }))!;
        Assert.Equal("R2", below["risk_without_restraint"]!.ToString());                              // never below the record-based baseline

        var absent = JsonNode.Parse(GroupWith(GrantText, 1, LitigationRiskTier.R1, a => a["risk"] = "R1"))!;
        Assert.Equal("R1", absent["risk_without_restraint"]!.ToString());                              // absent: the tier given
    }

    private const string LisText = "--- Page 1 (native) ---" + "\n" + "The respondent is restrained from alienating the property. A lis pendens notice is recorded against the property.";
    private const string LisEndedText = "--- Page 1 (native) ---" + "\n" + "The lis pendens notice is cancelled and removed from the record.";

    private static string RestraintAndLisR3(long id) => GroupWith(LisText, id, LitigationRiskTier.R1, a =>
    {
        a["risk"] = "R3"; a["risk_trigger"] = "Active restraint"; a["risk_reason"] = "Injunction in force.";
        a["active_restraint"] = true; a["restraint_type"] = "INJUNCTION"; a["lis_pendens"] = true;
        a["risk_without_restraint"] = "R1"; a["risk_trigger_without_restraint"] = "Pending Civil Suit"; a["risk_reason_without_restraint"] = "Ordinary pending matter.";
        a["evidence"] = new JsonArray(
            new JsonObject { ["claim"] = "restraint", ["order_id"] = id, ["quote"] = "The respondent is restrained from alienating the property" },
            new JsonObject { ["claim"] = "lis_pendens", ["order_id"] = id, ["quote"] = "A lis pendens notice is recorded against the property" },
            new JsonObject { ["claim"] = "risk_trigger", ["order_id"] = id, ["quote"] = "The respondent is restrained from alienating the property" });
    });

    private static string LisEndedR1(long id) => GroupWith(LisEndedText, id, LitigationRiskTier.R1, a =>
    {
        a["risk"] = "R1"; a["risk_trigger"] = "Pending Civil Suit"; a["risk_reason"] = "Pending civil matter.";
        a["lis_pendens_ended"] = true;
        a["evidence"] = new JsonArray(new JsonObject { ["claim"] = "lis_pendens_ended", ["order_id"] = id, ["quote"] = "The lis pendens notice is cancelled" });
    });

    [Fact]
    public void Ending_only_lis_pendens_does_not_downgrade_a_still_active_injunction()
    {
        var merged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([LisEndedR1(2), RestraintAndLisR3(1)]))!;

        Assert.True((bool)merged["active_restraint"]!);
        Assert.False((bool)merged["lis_pendens"]!);
        Assert.Equal("R3", merged["risk"]!.ToString());
        Assert.Equal("Active restraint", merged["risk_trigger"]!.ToString());
    }

    [Fact]
    public void Lifting_the_injunction_while_a_lis_pendens_remains_keeps_the_tier_and_ending_both_lowers_it()
    {
        var stillLis = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([LiftedR1(2), RestraintAndLisR3(1)]))!;
        Assert.False((bool)stillLis["active_restraint"]!);
        Assert.True((bool)stillLis["lis_pendens"]!);
        Assert.Equal("R3", stillLis["risk"]!.ToString());                 // the lis pendens is still in force
        // ...but the stated ground is no longer the lifted injunction
        Assert.Equal("Active lis pendens", stillLis["risk_trigger"]!.ToString());
        Assert.DoesNotContain("Injunction in force", stillLis["risk_reason"]!.ToString());
        Assert.Contains("lis pendens is still in force", stillLis["risk_reason"]!.ToString());
        Assert.Contains("injunction or restraint ended", stillLis["risk_reason"]!.ToString());

        // both ended, by two newer groups
        var bothEnded = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([LisEndedR1(3), LiftedR1(2), RestraintAndLisR3(1)]))!;
        Assert.False((bool)bothEnded["active_restraint"]!);
        Assert.False((bool)bothEnded["lis_pendens"]!);
        Assert.Equal("R1", bothEnded["risk"]!.ToString());

        // unchanged: a newer group that says nothing about either
        var unchanged = JsonNode.Parse(LitigationCaseAnalysisGroups.Merge([Quiet(2), RestraintAndLisR3(1)]))!;
        Assert.Equal("R3", unchanged["risk"]!.ToString());
        Assert.True((bool)unchanged["active_restraint"]!);
        Assert.True((bool)unchanged["lis_pendens"]!);
    }

    // ── Hearing dates are not part of the analysis input (plan 4.2d) ──

    [Fact]
    public void Neither_the_model_input_nor_the_stored_evidence_carries_a_hearing_date()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        c.LastHearingDate = "09-01-2025"; c.NextHearingDate = "20-10-2026";
        var input = Input(c, Docs((1, 11, OrderOneText)));

        Assert.DoesNotContain("hearing", input.CaseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hearing", input.EvidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("20-10-2026", input.CaseJson);
    }

    [Fact]
    public void A_refreshed_hearing_date_does_not_change_the_evidence_hash_but_a_status_change_does()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        var docs = Docs((1, 11, OrderOneText));
        var before = Input(c, docs);

        c.LastHearingDate = "01-09-2026"; c.NextHearingDate = "30-11-2026";
        Assert.Equal(before.EvidenceHash, Input(c, docs).EvidenceHash);          // a portal refresh of the hearing dates leaves the analysis current

        c.CaseStatus = "DISPOSED";
        Assert.NotEqual(before.EvidenceHash, Input(c, docs).EvidenceHash);       // the status bucket is still evidence
    }

    [Fact]
    public void The_prompt_no_longer_lists_hearing_dates_among_the_input_fields_and_says_not_to_state_them()
    {
        var text = LitigationCaseAnalysisPrompt.Instructions;
        Assert.DoesNotContain("last_hearing_date", text);
        Assert.DoesNotContain("next_hearing_date", text);
        Assert.Contains("do not state, estimate or comment on", text);
        Assert.Equal("2.2", LitigationCaseAnalysisPrompt.Version);
    }

    [Fact]
    public void An_answer_that_states_a_hearing_date_found_in_no_order_is_flagged_and_one_that_quotes_an_order_date_is_not()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        var input = Input(c, Docs((1, 11, OrderOneText + " The matter is adjourned to 12.12.2025 for arguments.")));

        var invented = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "The next hearing is on 05-11-2026 before the Tribunal."), input, LitigationRiskTier.R1, "t");
        Assert.True(invented.IsAccepted);
        Assert.Contains(invented.Notes, n => n.Contains("states a hearing date that is not in any order text"));

        var quoted = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "The order adjourns the matter to 12.12.2025 for arguments."), input, LitigationRiskTier.R1, "t");
        Assert.DoesNotContain(quoted.Notes, n => n.Contains("hearing date"));

        var unrelated = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "The loan was sanctioned on 05-11-2019."), input, LitigationRiskTier.R1, "t");
        Assert.DoesNotContain(unrelated.Notes, n => n.Contains("hearing date"));    // a date that is not near a hearing word is not a hearing date
    }

    [Fact]
    public void A_date_that_is_only_an_orders_header_date_does_not_support_it_as_a_hearing_date()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        // The order mentions 05-11-2026 only as its own date line, not as a hearing.
        var input = Input(c, Docs((1, 11, "Order dated 05-11-2026. " + OrderOneText)));
        var r = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "The next hearing is on 05-11-2026."), input, LitigationRiskTier.R1, "t");
        Assert.Contains(r.Notes, n => n.Contains("states a hearing date that is not in any order text"));
    }

    [Fact]
    public void A_hearing_date_written_before_its_hearing_words_is_still_checked_and_a_supported_one_is_not_flagged()
    {
        var c = NewCase((1, "09-01-2025", "Order"));
        var input = Input(c, Docs((1, 11, OrderOneText + " List the matter on 12.12.2025 for final hearing.")));

        var invented = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "On 05-11-2026, the next hearing, the Tribunal will hear it."), input, LitigationRiskTier.R1, "t");
        Assert.Contains(invented.Notes, n => n.Contains("hearing date"));

        var supported = LitigationCaseAnalysisValidator.Validate(Answer(input, a => a["summary"] = "On 12.12.2025, the final hearing, the matter is to be heard."), input, LitigationRiskTier.R1, "t");
        Assert.DoesNotContain(supported.Notes, n => n.Contains("hearing date"));
    }
}
