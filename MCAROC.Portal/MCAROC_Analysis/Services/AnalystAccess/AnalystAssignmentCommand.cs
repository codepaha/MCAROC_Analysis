using Microsoft.Extensions.DependencyInjection;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Interactive local operator entry point for the limited phase-one assignment workflow.</summary>
public static class AnalystAssignmentCommand
{
    public const string Argument = "--assign-analyst-request";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct = default)
    {
        if (args.Length != 1 || !string.Equals(args[0], Argument, StringComparison.Ordinal))
            return 2;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Analyst assignment requires an interactive local console.");
            return 2;
        }

        Console.Write("Request ID: ");
        if (!long.TryParse(Console.ReadLine(), out var requestId) || requestId <= 0)
        {
            Console.Error.WriteLine("A positive numeric request ID is required.");
            return 2;
        }
        Console.Write("Operator identifier (do not use an email address): ");
        var operatorId = Console.ReadLine() ?? string.Empty;
        Console.Write("Reason (optional; do not enter sensitive details): ");
        var reason = Console.ReadLine();

        try
        {
            using var scope = services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<AnalystAssignmentOperationService>()
                .AssignAsync(requestId, operatorId, reason, ct);
            Console.WriteLine(result.Changed
                ? $"Request {result.RequestId} assigned to Analyst {result.NewAnalystId}."
                : $"Request {result.RequestId} is already assigned to Analyst {result.NewAnalystId}; no change made.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
