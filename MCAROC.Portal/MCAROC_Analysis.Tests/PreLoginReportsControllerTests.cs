using System.Reflection;
using MCAROC_Analysis.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary><c>Mine()</c> is the one action on this anonymous, credential-scoped controller that
/// deliberately does zero server-side data access — it just serves a static shell for
/// prelogin-reports-mine.js to fill in from the browser's own localStorage. These tests lock down its
/// route contract (so "mine" can never silently collide with the {batch:guid}/... segments that ARE the
/// access credential for every other action) and confirm the action itself needs no service call.</summary>
public class PreLoginReportsControllerTests
{
    [Fact]
    public void Mine_returns_a_view_result_with_no_service_dependency_exercised()
    {
        var controller = new PreLoginReportsController(null!);

        var result = controller.Mine();

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.True(viewResult.ViewName is null or "Mine");
    }

    [Fact]
    public void Mine_action_is_routed_at_the_literal_segment_mine_via_HttpGet()
    {
        var method = typeof(PreLoginReportsController).GetMethod(nameof(PreLoginReportsController.Mine));
        Assert.NotNull(method);

        var httpGet = method!.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("mine", httpGet!.Template);
    }
}
