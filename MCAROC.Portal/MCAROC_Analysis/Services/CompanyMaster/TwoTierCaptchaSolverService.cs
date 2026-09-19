using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public interface ITwoTierCaptchaSolverService
{
    Task<string?> SolveAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}

public sealed partial class TwoTierCaptchaSolverService : ITwoTierCaptchaSolverService
{
    private readonly string _tesseractPath;
    private readonly ILogger<TwoTierCaptchaSolverService> _logger;

    [GeneratedRegex("^[a-zA-Z0-9]{4,8}$")]
    private static partial Regex AlphanumericCaptchaRegex();

    public TwoTierCaptchaSolverService(IConfiguration configuration, ILogger<TwoTierCaptchaSolverService> logger)
    {
        _logger = logger;
        _tesseractPath = configuration["CompanyMasterSync:TesseractPath"] ?? @"C:\Program Files\Tesseract-OCR\tesseract.exe";
    }

    public async Task<string?> SolveAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (imageBytes == null || imageBytes.Length == 0) return null;

        // Tier 1: Local Tesseract OCR
        string? result = await TrySolveWithTesseractAsync(imageBytes, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result) && AlphanumericCaptchaRegex().IsMatch(result))
        {
            _logger.LogInformation("Captcha resolved via Tier-1 local Tesseract.");
            return result;
        }

        // Tier 2: Secondary / Fallback placeholder (e.g. Gemini or heuristic retry)
        _logger.LogInformation("Tier-1 OCR yielded invalid/low-confidence captcha. Attempting Tier-2 fallback.");
        return result;
    }

    private async Task<string?> TrySolveWithTesseractAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        if (!File.Exists(_tesseractPath))
        {
            _logger.LogWarning("Tesseract binary not found at '{Path}'. Skipping Tier-1 OCR.", _tesseractPath);
            return null;
        }

        string tempImage = Path.Combine(Path.GetTempPath(), $"mca_cap_{Guid.NewGuid():N}.png");
        string tempOutputBase = Path.Combine(Path.GetTempPath(), $"mca_txt_{Guid.NewGuid():N}");
        string tempOutputFile = tempOutputBase + ".txt";

        try
        {
            await File.WriteAllBytesAsync(tempImage, imageBytes, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = _tesseractPath,
                Arguments = $"\"{tempImage}\" \"{tempOutputBase}\" --psm 7 -c tessedit_char_whitelist=abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            using (cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }))
            {
                await process.WaitForExitAsync(cancellationToken);
            }

            if (File.Exists(tempOutputFile))
            {
                string text = await File.ReadAllTextAsync(tempOutputFile, cancellationToken);
                return text.Trim().Replace(" ", string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tesseract execution failed");
        }
        finally
        {
            // Ephemeral cleanup: Never leave CAPTCHA images on disk
            try { if (File.Exists(tempImage)) File.Delete(tempImage); } catch { }
            try { if (File.Exists(tempOutputFile)) File.Delete(tempOutputFile); } catch { }
        }

        return null;
    }
}
