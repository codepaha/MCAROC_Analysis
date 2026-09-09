using System.Diagnostics;
using MCAROC_Analysis.Models;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers
{
    public class HomeController : Controller
    {
        // Index/Privacy were the ASP.NET scaffold placeholders — removed with the navbar overhaul.
        // Error stays: Program.cs wires app.UseExceptionHandler("/Home/Error").

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
