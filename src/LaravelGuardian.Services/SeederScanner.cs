using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Reads database/seeders and database/factories (read-only) and suggests test accounts.
/// Heuristic: it only sees literal values. env() passwords or random factory emails give nothing.
public class SeederScanner : ISeederScanner
{
    private static readonly Regex Email = new(
        @"['""]email['""]\s*=>\s*['""]([^'""\s]+@[^'""\s]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Role = new(
        @"['""]role['""]\s*=>\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AssignRole = new(
        @"assignRole\(\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Password = new(
        @"['""]password['""]\s*=>\s*(?:\\?Hash::make|bcrypt)\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FactoryPassword = new(
        @"(?:\\?Hash::make|bcrypt)\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<SeededAccount>> ScanAsync(string projectPath, CancellationToken ct = default)
    {
        var accounts = new List<SeededAccount>();
        var seedersDir = Path.Combine(projectPath, "database", "seeders");
        var factoriesDir = Path.Combine(projectPath, "database", "factories");
        if (!Directory.Exists(seedersDir)) return accounts;

        // Default password used by the project's factories, if it is a literal.
        string? factoryPassword = null;
        if (Directory.Exists(factoriesDir))
        {
            foreach (var file in Directory.EnumerateFiles(factoriesDir, "*.php", SearchOption.AllDirectories))
            {
                var text = await ReadAsync(file, ct);
                var m = FactoryPassword.Match(text);
                if (m.Success) { factoryPassword = m.Groups[1].Value; break; }
            }
        }

        foreach (var file in Directory.EnumerateFiles(seedersDir, "*.php", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var text = await ReadAsync(file, ct);
            var relative = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
            var matches = Email.Matches(text);

            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var prevEnd = i == 0 ? 0 : matches[i - 1].Index + matches[i - 1].Length;
                var nextStart = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var start = Math.Max(prevEnd, m.Index - 400);
                var end = Math.Min(nextStart, m.Index + m.Length + 800);
                var window = text[start..end];

                var role = Role.Match(window) is { Success: true } r1 ? r1.Groups[1].Value
                         : AssignRole.Match(window) is { Success: true } r2 ? r2.Groups[1].Value
                         : null;

                string? password = Password.Match(window) is { Success: true } p ? p.Groups[1].Value : null;
                var source = relative;
                if (password is null && factoryPassword is not null)
                {
                    password = factoryPassword;
                    source += " (password from factory)";
                }

                accounts.Add(new SeededAccount(m.Groups[1].Value, role, password, source));
            }
        }

        return accounts
            .GroupBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.Password is not null).ThenByDescending(a => a.Role is not null).First())
            .ToList();
    }

    private static async Task<string> ReadAsync(string file, CancellationToken ct)
    {
        try { return await File.ReadAllTextAsync(file, ct); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}