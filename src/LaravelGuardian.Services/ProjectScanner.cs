// ProjectScanner.cs
using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class ProjectScanner : IProjectScanner
{
    private static readonly string[] RequiredFiles = { "artisan", "composer.json" };
    private static readonly string[] RequiredDirs = { "app", "routes", "public", "storage" };

    public async Task<ProjectInfo> ScanAsync(string path, CancellationToken ct = default)
    {
        var missing = new List<string>();
        missing.AddRange(RequiredFiles.Where(f => !File.Exists(Path.Combine(path, f))));
        missing.AddRange(RequiredDirs.Where(d => !Directory.Exists(Path.Combine(path, d))));

        string? laravelVersion = null;
        bool pest = false, phpunit = false;
        var composerPath = Path.Combine(path, "composer.json");
        if (File.Exists(composerPath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(composerPath, ct));
            var deps = Merge(doc.RootElement, "require", "require-dev");
            laravelVersion = deps.GetValueOrDefault("laravel/framework");
            pest = deps.ContainsKey("pestphp/pest");
            phpunit = deps.ContainsKey("phpunit/phpunit");
        }

        bool vite = false;
        string? frontend = null;
        var pkgPath = Path.Combine(path, "package.json");
        if (File.Exists(pkgPath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pkgPath, ct));
            var deps = Merge(doc.RootElement, "dependencies", "devDependencies");
            vite = deps.ContainsKey("vite");
            frontend = deps.ContainsKey("react") ? "React" : deps.ContainsKey("vue") ? "Vue" : null;
        }

        return new ProjectInfo
        {
            Path = path,
            IsLaravel = missing.Count == 0 && laravelVersion is not null,
            MissingItems = missing,
            LaravelVersion = laravelVersion,
            HasPest = pest,
            HasPhpUnit = phpunit,
            UsesVite = vite,
            FrontendFramework = frontend
        };
    }

    private static Dictionary<string, string> Merge(JsonElement root, params string[] sections)
    {
        var result = new Dictionary<string, string>();
        foreach (var s in sections)
            if (root.TryGetProperty(s, out var el) && el.ValueKind == JsonValueKind.Object)
                foreach (var p in el.EnumerateObject())
                    result[p.Name] = p.Value.GetString() ?? "";
        return result;
    }
}