using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class ControllerChecker : IControllerChecker
{
    public Task<IReadOnlyList<TestResult>> CheckAsync(
        ProjectInfo project, IReadOnlyList<RouteInfo> routes, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<TestResult>>(() => new Scope(project.Path).Run(routes, ct), ct);

    // ------------------------------------------------------------------

    private sealed class ClassFile
    {
        public string Path { get; init; } = "";
        public string Raw { get; init; } = "";
        public string Code { get; init; } = "";
        public bool Declared { get; set; }
        public string? Parent { get; set; }
        public List<string> Traits { get; } = new();

        /// Method name -> "public", "protected" or "private".
        public Dictionary<string, string> Methods { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Scope
    {
        // Framework types that never define route actions, so they never make a result uncertain
        private static readonly HashSet<string> KnownHarmless = new(StringComparer.OrdinalIgnoreCase)
        {
            @"Illuminate\Routing\Controller",
            @"Illuminate\Foundation\Auth\Access\AuthorizesRequests",
            @"Illuminate\Foundation\Validation\ValidatesRequests",
            @"Illuminate\Foundation\Bus\DispatchesJobs"
        };

        // Captures the modifiers in front of "function name(" so visibility can be checked.
        private static readonly Regex FunctionDecl = new(
            @"((?:\b(?:abstract|final|public|protected|private|static)\s+)*)function\s+&?\s*([A-Za-z_]\w*)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NamespaceRx = new(
            @"\bnamespace\s+([\\\w]+)\s*;", RegexOptions.Compiled);
        private static readonly Regex SimpleImport = new(
            @"^\s*use\s+([\\\w]+?)(?:\s+as\s+(\w+))?\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex GroupImport = new(
            @"^\s*use\s+([\\\w]+)\\\{([^}]+)\}\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex ImportPart = new(
            @"^([\\\w]+)(?:\s+as\s+(\w+))?$", RegexOptions.Compiled);

        // Lines of vendor/composer/autoload_psr4.php, e.g.  'App\\' => array($baseDir . '/app'),
        private static readonly Regex InstalledPsr4Line = new(
            @"^\s*'(?<prefix>(?:[^'\\]|\\.)*)'\s*=>\s*array\((?<dirs>.*)\)\s*,?\s*$", RegexOptions.Compiled);
        private static readonly Regex BaseDirPart = new(
            @"\$baseDir\s*\.\s*'(?<dir>[^']*)'", RegexOptions.Compiled);

        private readonly string _root;
        private readonly List<(string Prefix, List<string> Dirs)> _prefixes = new();
        private readonly Dictionary<string, ClassFile?> _cache = new();

        public Scope(string root)
        {
            _root = root;

            // 1) composer.json: the project's own PSR-4 mappings
            try
            {
                var composer = System.IO.Path.Combine(root, "composer.json");
                if (File.Exists(composer))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(composer));
                    if (doc.RootElement.TryGetProperty("autoload", out var autoload)
                        && autoload.TryGetProperty("psr-4", out var psr4)
                        && psr4.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in psr4.EnumerateObject())
                        {
                            if (p.Value.ValueKind == JsonValueKind.String)
                                AddMapping(p.Name, p.Value.GetString()!);
                            else if (p.Value.ValueKind == JsonValueKind.Array)
                                foreach (var d in p.Value.EnumerateArray())
                                    if (d.ValueKind == JsonValueKind.String) AddMapping(p.Name, d.GetString()!);
                        }
                    }
                }
            }
            catch { /* fall back to the other sources below */ }

            // 2) vendor/composer/autoload_psr4.php: also covers modules and merged composer files.
            //    Only entries that point into the project (not vendor/) are taken.
            try
            {
                var installed = System.IO.Path.Combine(root, "vendor", "composer", "autoload_psr4.php");
                if (File.Exists(installed))
                {
                    foreach (var line in File.ReadLines(installed))
                    {
                        var m = InstalledPsr4Line.Match(line);
                        if (!m.Success) continue;

                        var prefix = m.Groups["prefix"].Value.Replace(@"\\", @"\");
                        foreach (Match d in BaseDirPart.Matches(m.Groups["dirs"].Value))
                            AddMapping(prefix, d.Groups["dir"].Value);
                    }
                }
            }
            catch { /* optional source */ }

            if (_prefixes.Count == 0) AddMapping(@"App\", "app");
            _prefixes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length)); // longest prefix first
        }

        private void AddMapping(string prefix, string dir)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            dir = dir.Trim().Trim('/', '\\');

            var idx = _prefixes.FindIndex(p => p.Prefix == prefix);
            if (idx < 0)
                _prefixes.Add((prefix, new List<string> { dir }));
            else if (!_prefixes[idx].Dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                _prefixes[idx].Dirs.Add(dir);
        }

        // ---------- main run ----------

        public List<TestResult> Run(IReadOnlyList<RouteInfo> routes, CancellationToken ct)
        {
            var targets = new Dictionary<(string Cls, string Method), List<RouteInfo>>();
            var skipped = new Dictionary<string, List<RouteInfo>>();

            void Skip(string code, RouteInfo r)
            {
                if (!skipped.TryGetValue(code, out var list)) skipped[code] = list = new List<RouteInfo>();
                list.Add(r);
            }

            foreach (var r in routes)
            {
                var action = r.Action?.Trim();
                if (string.IsNullOrEmpty(action) || action.Equals("Closure", StringComparison.OrdinalIgnoreCase))
                { Skip("closure", r); continue; }

                // "Class@method" is a normal action; a bare class name is an invokable controller.
                var at = action.IndexOf('@');
                var cls = (at < 0 ? action : action[..at]).TrimStart('\\');
                var method = at < 0 ? "__invoke" : action[(at + 1)..];

                if (!IsProjectClass(cls)) { Skip("vendor", r); continue; }

                var key = (cls, method);
                if (!targets.TryGetValue(key, out var list)) targets[key] = list = new List<RouteInfo>();
                list.Add(r);
            }

            var results = new List<TestResult>();

            foreach (var ((cls, method), list) in targets.OrderBy(t => t.Key.Cls).ThenBy(t => t.Key.Method))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(Evaluate(cls, method, list));
            }

            foreach (var (code, list) in skipped)
            {
                var label = code switch
                {
                    "closure" => "closures",
                    "vendor" => "vendor or framework controllers",
                    _ => code
                };
                var r = new TestResult
                {
                    Category = "Controllers",
                    Name = $"Route actions not checked: {label}",
                    Status = TestStatus.Skipped,
                    Severity = Severity.Info,
                    Message = $"{list.Count} route(s): {label}"
                };
                r.Metadata["skipReason"] = code;
                r.Metadata["routes"] = Describe(list);
                results.Add(r);
            }

            return results;
        }

        // ---------- one controller method ----------

        private TestResult Evaluate(string cls, string method, List<RouteInfo> routes)
        {
            var result = new TestResult
            {
                Category = "Controllers",
                Name = $"{cls}::{method}()",
                Expected = "Method exists"
            };
            result.Metadata["routes"] = Describe(routes);
            result.Metadata["routeCount"] = routes.Count.ToString();

            var path = FindFile(cls, out var exists);
            if (path is not null) result.Metadata["file"] = System.IO.Path.GetRelativePath(_root, path);

            if (!exists)
                return Set(result, TestStatus.Fail, Severity.High, "file not found",
                    "Controller class file not found" +
                    (path is null ? "" : $" (expected {System.IO.Path.GetRelativePath(_root, path)})"));

            var file = LoadClass(cls);
            if (file is null || !file.Declared)
                return Set(result, TestStatus.Fail, Severity.High, "class not declared",
                    "The file exists but does not declare this class");

            var unknown = new List<string>();
            if (Search(cls, method, new HashSet<string>(), unknown, out var foundIn, out var visibility))
            {
                if (visibility != "public")
                    return Set(result, TestStatus.Fail, Severity.High, "not public",
                        $"Method is {visibility}, so the router cannot call it" +
                        (foundIn == cls ? "" : $" (declared in {foundIn})"));

                return Set(result, TestStatus.Pass, Severity.Info, "found",
                    foundIn == cls ? "Method found" : $"Method found in {foundIn}");
            }

            var commented = Regex.IsMatch(file.Raw,
                $@"\bfunction\s+&?\s*{Regex.Escape(method)}\s*\(", RegexOptions.IgnoreCase);

            if (unknown.Count == 0)
                return Set(result, TestStatus.Fail, Severity.High, "missing",
                    commented
                        ? "Method exists only inside a comment (commented out)"
                        : "Method does not exist in the controller");

            return Set(result, TestStatus.Warning, Severity.Medium, "uncertain",
                "Not defined in the project files; it may come from " + string.Join(", ", unknown) +
                (commented ? " (a commented-out copy exists)" : ""));
        }

        private static TestResult Set(TestResult r, TestStatus status, Severity severity, string actual, string message)
        {
            r.Status = status;
            r.Severity = severity;
            r.Actual = actual;
            r.Message = message;
            return r;
        }

        /// Looks in the class, its traits and its parents (project files only).
        private bool Search(string fqcn, string method, HashSet<string> visited,
            List<string> unknown, out string? foundIn, out string visibility)
        {
            foundIn = null;
            visibility = "public";
            if (!visited.Add(fqcn)) return false;

            var cls = LoadClass(fqcn);
            if (cls is null || !cls.Declared)
            {
                if (!KnownHarmless.Contains(fqcn)) unknown.Add(fqcn);
                return false;
            }

            if (cls.Methods.TryGetValue(method, out var vis))
            {
                foundIn = fqcn;
                visibility = vis;
                return true;
            }
            if (cls.Methods.ContainsKey("__call")) unknown.Add($"{fqcn} (__call)");

            foreach (var trait in cls.Traits)
                if (Search(trait, method, visited, unknown, out foundIn, out visibility)) return true;

            if (cls.Parent is not null
                && Search(cls.Parent, method, visited, unknown, out foundIn, out visibility))
                return true;

            foundIn = null;
            visibility = "public";
            return false;
        }

        // ---------- class loading and parsing ----------

        private bool IsProjectClass(string fqcn) =>
            _prefixes.Any(p => fqcn.StartsWith(p.Prefix, StringComparison.Ordinal));

        private string? FindFile(string fqcn, out bool exists)
        {
            exists = false;
            string? firstCandidate = null;

            foreach (var (prefix, dirs) in _prefixes)
            {
                if (!fqcn.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = fqcn[prefix.Length..].Replace('\\', System.IO.Path.DirectorySeparatorChar) + ".php";

                foreach (var dir in dirs)
                {
                    var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        _root, dir.Replace('/', System.IO.Path.DirectorySeparatorChar), rel));
                    firstCandidate ??= candidate;
                    if (File.Exists(candidate)) { exists = true; return candidate; }
                }
            }
            return firstCandidate;
        }

        private ClassFile? LoadClass(string fqcn)
        {
            if (_cache.TryGetValue(fqcn, out var cached)) return cached;

            ClassFile? info = null;
            var path = FindFile(fqcn, out var exists);
            if (path is not null && exists)
            {
                try { info = Parse(fqcn, path); }
                catch (IOException) { info = null; }
            }

            _cache[fqcn] = info;
            return info;
        }

        private ClassFile Parse(string fqcn, string path)
        {
            var raw = File.ReadAllText(path);
            var code = StripComments(raw);
            var shortName = fqcn[(fqcn.LastIndexOf('\\') + 1)..];

            var info = new ClassFile { Path = path, Raw = raw, Code = code };
            foreach (Match m in FunctionDecl.Matches(code))
            {
                var modifiers = m.Groups[1].Value.ToLowerInvariant();
                var visibility = modifiers.Contains("private") ? "private"
                    : modifiers.Contains("protected") ? "protected"
                    : "public";
                info.Methods.TryAdd(m.Groups[2].Value, visibility);
            }

            var ns = NamespaceRx.Match(code);
            var nsName = ns.Success ? ns.Groups[1].Value : "";

            var decl = Regex.Match(code,
                $@"\b(?:class|trait)\s+{Regex.Escape(shortName)}\b(?:\s+extends\s+([\\\w]+))?");
            if (!decl.Success) return info;
            info.Declared = true;

            var imports = ReadImports(code[..decl.Index]);
            if (decl.Groups[1].Success)
                info.Parent = Resolve(decl.Groups[1].Value, nsName, imports);

            var brace = code.IndexOf('{', decl.Index + decl.Length);
            if (brace >= 0)
                foreach (var trait in ReadTraits(code, brace))
                    info.Traits.Add(Resolve(trait, nsName, imports));

            return info;
        }

        private static Dictionary<string, string> ReadImports(string header)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string full, string? alias)
            {
                full = full.Trim().TrimStart('\\');
                var key = string.IsNullOrEmpty(alias) ? full[(full.LastIndexOf('\\') + 1)..] : alias;
                map[key] = full;
            }

            foreach (Match m in GroupImport.Matches(header))
            {
                var prefix = m.Groups[1].Value.TrimEnd('\\');
                foreach (var part in m.Groups[2].Value.Split(',',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pm = ImportPart.Match(part);
                    if (pm.Success)
                        Add(prefix + "\\" + pm.Groups[1].Value, pm.Groups[2].Success ? pm.Groups[2].Value : null);
                }
            }

            foreach (Match m in SimpleImport.Matches(header))
                Add(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null);

            return map;
        }

        private static string Resolve(string name, string ns, Dictionary<string, string> imports)
        {
            name = name.Trim();
            if (name.StartsWith('\\')) return name.TrimStart('\\');

            var first = name.Split('\\')[0];
            if (imports.TryGetValue(first, out var full))
                return name.Length == first.Length ? full : full + name[first.Length..];

            return string.IsNullOrEmpty(ns) ? name : ns + "\\" + name;
        }

        /// Trait `use` statements sit directly inside the class body (brace depth 1).
        private static List<string> ReadTraits(string code, int classBrace)
        {
            var traits = new List<string>();
            int depth = 0;

            for (int i = classBrace; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '{') { depth++; continue; }
                if (c == '}') { depth--; if (depth <= 0) break; continue; }

                if (depth == 1 && c == 'u' && IsUseKeyword(code, i))
                {
                    int end = i + 3;
                    while (end < code.Length && code[end] != ';' && code[end] != '{') end++;

                    foreach (var name in code[(i + 3)..end].Split(',',
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        traits.Add(name);

                    i = end - 1;
                }
            }
            return traits;
        }

        private static bool IsUseKeyword(string code, int i)
        {
            if (i + 3 >= code.Length || code[i + 1] != 's' || code[i + 2] != 'e') return false;
            if (!char.IsWhiteSpace(code[i + 3])) return false;
            if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_' || code[i - 1] == '$')) return false;
            return true;
        }

        /// Removes // # and /* */ comments but leaves strings alone. PHP 8 attributes (#[...]) are kept.
        private static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);
            int i = 0, n = src.Length;

            while (i < n)
            {
                char c = src[i];
                char next = i + 1 < n ? src[i + 1] : '\0';

                if (c == '\'' || c == '"')
                {
                    char quote = c;
                    sb.Append(c);
                    i++;
                    while (i < n)
                    {
                        char d = src[i];
                        sb.Append(d);
                        i++;
                        if (d == '\\' && i < n) { sb.Append(src[i]); i++; continue; }
                        if (d == quote) break;
                    }
                    continue;
                }

                if ((c == '/' && next == '/') || (c == '#' && next != '['))
                {
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++;
                    i = Math.Min(n, i + 2);
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string Describe(List<RouteInfo> list)
        {
            var text = string.Join("; ", list.Take(8)
                .Select(x => $"{string.Join("|", x.Methods)} /{x.Uri.TrimStart('/')}"));
            return list.Count > 8 ? $"{text} (+{list.Count - 8} more)" : text;
        }
    }
}