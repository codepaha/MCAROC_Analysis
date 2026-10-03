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
        var orders = Enumerable.Range(1, 9).Select(i => ((long)i, $"0{i}-01-2025", "Order")).ToArray();
        var docs = Docs(orders.Select(o => (o.Item1, o.Item1 + 100, (string?)new string('x', 55_000))).ToArray());
        var input = Input(NewCase(orders), docs);

        Assert.True(input.TextTruncated);
        Assert.NotNull(input.Orders.First(o => o.OrderId == 9).Text);   // newest keeps its text
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
}
