using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// One test account per project folder, in %LocalAppData%\LaravelGuardian\accounts.json.
/// The password is encrypted with Windows DPAPI for the current Windows user only.
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
        public string Secret { get; set; } = "";
    }

    public void Save(string projectPath, string email, string password)
    {
        lock (_gate)
        {
            var all = ReadAll();
            all[Key(projectPath)] = new Entry { Email = email, Secret = Protect(password) };
            WriteAll(all);
        }
    }

    public AuthProfile? Load(string projectPath)
    {
        lock (_gate)
        {
            if (!ReadAll().TryGetValue(Key(projectPath), out var entry)) return null;

            string? password = null;
            try { password = Unprotect(entry.Secret); }
            catch (CryptographicException) { /* saved by another Windows user: ask again */ }
            catch (FormatException) { }

            return new AuthProfile { ProjectPath = projectPath, Email = entry.Email, Password = password };
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

    private Dictionary<string, Entry> ReadAll()
    {
        try
        {
            if (!File.Exists(_file)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    private void WriteAll(Dictionary<string, Entry> all)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }
}