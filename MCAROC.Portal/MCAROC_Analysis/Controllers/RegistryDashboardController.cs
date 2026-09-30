using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using NPOI.XSSF.UserModel;

namespace MCAROC_Analysis.Controllers;

[Route("registry")]
public sealed class RegistryDashboardController : Controller
{
    private readonly CompanyRegistryQueryService _queryService;

    public RegistryDashboardController(CompanyRegistryQueryService queryService)
    {
        _queryService = queryService;
    }

    [HttpGet("")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Index(
        [FromQuery] string? tab = null,
        [FromQuery] RegistryExplorerCriteria? explorer = null,
        CancellationToken ct = default)
    {
        var model = await _queryService.GetDashboardAsync(tab, explorer, ct);
        return RenderDashboard(model);
    }

    [HttpGet("explorer")]
    public async Task<IActionResult> Explorer(
        [FromQuery] RegistryExplorerCriteria criteria,
        CancellationToken ct = default)
    {
        var model = await _queryService.GetDashboardAsync("explorer", criteria, ct);
        return RenderDashboard(model);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] RegistryExplorerCriteria criteria, CancellationToken ct = default)
    {
        IReadOnlyList<RegistryRecordRow> rows;
        string? error;
        try
        {
            (rows, error) = await _queryService.ExportExplorerAsync(criteria, ct);
        }
        catch (SqlException ex) when (ex.Number == -2 && !ct.IsCancellationRequested)
        {
            return StatusCode(503, "Registry export timed out. Narrow the filters and try again.");
        }
        if (error != null) return BadRequest(error);

        using var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Registry Records");
        string[] headers = ["Identifier", "Entity Name", "Type", "Current Status", "Registration Date", "State / UT", "District", "ROC", "Class", "Industry", "Address", "PIN Code", "Listing Status", "Country"];
        var header = sheet.CreateRow(0);
        for (var col = 0; col < headers.Length; col++) header.CreateCell(col).SetCellValue(headers[col]);
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            var row = sheet.CreateRow(i + 1);
            string?[] values = [item.Identifier, item.Name, item.RecordType.ToString(), item.Status,
                item.RegistrationDate?.ToString("yyyy-MM-dd"), item.State, item.District, item.Roc, item.Class,
                item.Industry, item.Address, item.PinCode, item.ListingStatus, item.Country];
            for (var col = 0; col < values.Length; col++) row.CreateCell(col).SetCellValue(values[col] ?? "");
        }
        sheet.CreateFreezePane(0, 1);
        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"registry-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }
    private IActionResult RenderDashboard(RegistryDashboardViewModel model)
    {
        if (model.State == RegistrySnapshotState.DatabaseUnavailable)
        {
            Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable;
            Response.Headers["Retry-After"] = "30";
        }
        else if (model.Explorer?.ValidationErrorMessage != null)
        {
            Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest;
        }
        return View("~/Views/Registry/Index.cshtml", model);
    }
}

