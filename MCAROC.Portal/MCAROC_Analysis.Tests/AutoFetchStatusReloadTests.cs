using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Tests;

/// <summary>#514 follow-up: the approve-unlock box on the request page is drawn by the server, so a job that starts waiting for an approval after the page
/// loaded only showed the box after a manual refresh. The status the page polls now says when the page must reload.</summary>
public sealed class AutoFetchStatusReloadTests
{
    private static AutoFetchJob Job(AutoFetchJobStatus status) => new() { Status = status, Cin = "U1", Bid = "b", WarningsJson = "[]" };

    [Theory]
    [InlineData("CheckingSession", AutoFetchJobStatus.WaitingForUnlock)]   // started waiting after the page loaded: show the box
    [InlineData("Queued", AutoFetchJobStatus.WaitingForUnlock)]
    [InlineData("WaitingForUnlock", AutoFetchJobStatus.Queued)]            // approved elsewhere (the alert): hide the box
    [InlineData("WaitingForUnlock", AutoFetchJobStatus.FetchingWorkbooks)]
    public void Entering_or_leaving_the_wait_for_an_unlock_approval_reloads_the_page(string shown, AutoFetchJobStatus now) =>
        Assert.True(AutoFetchController.ShouldReloadPage(shown, Job(now)));

    [Theory]
    [InlineData("WaitingForUnlock", AutoFetchJobStatus.WaitingForUnlock)]  // unchanged
    [InlineData("CheckingSession", AutoFetchJobStatus.FetchingWorkbooks)]  // progress the page updates in place
    [InlineData("FetchingWorkbooks", AutoFetchJobStatus.WaitingForRefresh)]
    [InlineData(null, AutoFetchJobStatus.WaitingForUnlock)]                // an old page that does not say what it shows
    [InlineData("not-a-status", AutoFetchJobStatus.WaitingForUnlock)]
    public void Other_changes_update_in_place_without_a_reload(string? shown, AutoFetchJobStatus now) =>
        Assert.False(AutoFetchController.ShouldReloadPage(shown, Job(now)));

    [Theory]
    [InlineData(AutoFetchJobStatus.Completed)]
    [InlineData(AutoFetchJobStatus.Failed)]
    public void A_finished_job_always_reloads_as_before(AutoFetchJobStatus now) =>
        Assert.True(AutoFetchController.ShouldReloadPage("FetchingWorkbooks", Job(now)));
}
