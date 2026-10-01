using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaravelGuardian.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaravelGuardian");

    public static string DbPath => Path.Combine(Root, "guardian.db");
    public static string RunDir(Guid runId) => Path.Combine(Root, "runs", runId.ToString("N"));
    public static string EvidenceDir(Guid runId) => Path.Combine(RunDir(runId), "evidence");
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}