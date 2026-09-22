using Microsoft.Extensions.DependencyInjection;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Local, interactive bootstrap command. Password input is never accepted as an argument or echoed.</summary>
public static class AnalystProvisioningCommand
{
    public const string Argument = "--provision-analyst";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct = default)
    {
        if (args.Length != 1 || !string.Equals(args[0], Argument, StringComparison.Ordinal))
            return 2;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Analyst provisioning requires an interactive local console.");
            return 2;
        }

        Console.Write("Login name: ");
        var loginName = Console.ReadLine() ?? string.Empty;
        Console.Write("Display name: ");
        var displayName = Console.ReadLine() ?? string.Empty;
        var password = ReadPassword("Password (16+ characters): ");
        var confirmation = ReadPassword("Confirm password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 2;
        }

        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AnalystProvisioningService>()
                .ProvisionInitialAsync(loginName, displayName, password, ct);
            Console.WriteLine("Initial Analyst account created.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                buffer.Length--;
            else if (!char.IsControl(key.KeyChar))
                buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}
