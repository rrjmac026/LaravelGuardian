using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Test accounts per project folder (one per role), in %LocalAppData%\LaravelGuardian\accounts.json.
/// Passwords are encrypted with Windows DPAPI for the current Windows user only.
/// The older single-account file format is still read and is upgraded the next time something is saved.
public class SecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LaravelGuardian.accounts.v1");
    private readonly string _file;
    private readonly object _gate = new();

    public SecretStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaravelGuardian");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "accounts.json");
    }

    private sealed class Entry
    {
        public string Email { get; set; } = "";
        public string? Role { get; set; }
        public string Secret { get; set; } = "";
    }

    public void Save(string projectPath, string email, string? role, string password)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var key = Key(projectPath);
            if (!all.TryGetValue(key, out var list)) all[key] = list = new List<Entry>();

            list.RemoveAll(e => e.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            list.Add(new Entry
            {
                Email = email,
                Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
                Secret = Protect(password)
            });
            WriteAll(all);
        }
    }

    public IReadOnlyList<AuthProfile> LoadAll(string projectPath)
    {
        lock (_gate)
        {
            var profiles = new List<AuthProfile>();
            if (!ReadAll().TryGetValue(Key(projectPath), out var list)) return profiles;

            foreach (var entry in list)
            {
                string? password = null;
                try { password = Unprotect(entry.Secret); }
                catch (CryptographicException) { /* saved by another Windows user: ask again */ }
                catch (FormatException) { }

                profiles.Add(new AuthProfile
                {
                    ProjectPath = projectPath,
                    Email = entry.Email,
                    Role = entry.Role,
                    Password = password
                });
            }
            return profiles;
        }
    }

    public void Delete(string projectPath, string email)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var key = Key(projectPath);
            if (!all.TryGetValue(key, out var list)) return;

            if (list.RemoveAll(e => e.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) == 0) return;
            if (list.Count == 0) all.Remove(key);
            WriteAll(all);
        }
    }

    public void Delete(string projectPath)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (all.Remove(Key(projectPath))) WriteAll(all);
        }
    }

    private static string Key(string projectPath) =>
        Path.GetFullPath(projectPath).TrimEnd('\\', '/').ToLowerInvariant();

    private static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI needs Windows.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI needs Windows.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    // ---------- file ----------

    /// Reads both formats: a project key holding an array of entries (new) or one entry object (old).
    private Dictionary<string, List<Entry>> ReadAll()
    {
        var all = new Dictionary<string, List<Entry>>();
        try
        {
            if (!File.Exists(_file)) return all;

            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var list = new List<Entry>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray()) AddEntry(list, item);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    AddEntry(list, prop.Value);
                }
                if (list.Count > 0) all[prop.Name] = list;
            }
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        return all;
    }

    private static void AddEntry(List<Entry> list, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return;

        string? Str(string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        var email = Str("Email");
        var secret = Str("Secret");
        if (string.IsNullOrWhiteSpace(email) || secret is null) return;

        var role = Str("Role");
        list.Add(new Entry { Email = email, Role = string.IsNullOrWhiteSpace(role) ? null : role, Secret = secret });
    }

    private void WriteAll(Dictionary<string, List<Entry>> all)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }
}