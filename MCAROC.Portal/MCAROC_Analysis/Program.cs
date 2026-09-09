using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using Microsoft.EntityFrameworkCore;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy .xls encodings

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<IExcelSheetReader, ExcelSheetReader>();
builder.Services.AddScoped<FileValidationService>();
builder.Services.AddScoped<IngestionOrchestrator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
