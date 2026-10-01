using System.Globalization;
using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Data.Sqlite;

namespace LaravelGuardian.Services;

public sealed class RunStore : IRunStore
{
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS test_runs (
    id TEXT PRIMARY KEY, project_name TEXT NOT NULL, project_path TEXT NOT NULL,
    started_at TEXT NOT NULL, updated_at TEXT NOT NULL,
    total INTEGER NOT NULL DEFAULT 0, passed INTEGER NOT NULL DEFAULT 0,
    failed INTEGER NOT NULL DEFAULT 0, warnings INTEGER NOT NULL DEFAULT 0,
    skipped INTEGER NOT NULL DEFAULT 0, blocked INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS test_results (
    id TEXT PRIMARY KEY, run_id TEXT NOT NULL, source TEXT NOT NULL,
    category TEXT, name TEXT, status TEXT, severity TEXT, started_at TEXT,
    duration_ms INTEGER, url TEXT, http_status INTEGER, message TEXT,
    evidence_path TEXT, json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_results_run ON test_results(run_id, source);";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _ready;

    public RunStore()
    {
        Directory.CreateDirectory(AppPaths.Root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = AppPaths.DbPath }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        if (!_ready)
        {
            await _initGate.WaitAsync();
            try
            {
                if (!_ready)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = Schema;
                    await cmd.ExecuteNonQueryAsync();
                    _ready = true;
                }
            }
            finally { _initGate.Release(); }
        }
        return conn;
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<Guid> CreateRunAsync(string projectName, string projectPath)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO test_runs(id,project_name,project_path,started_at,updated_at)
                            VALUES($id,$n,$p,$t,$t)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$n", projectName);
        cmd.Parameters.AddWithValue("$p", projectPath);
        cmd.Parameters.AddWithValue("$t", Now());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<IReadOnlyList<string>> ReplaceSourceAsync(
        Guid runId, string source, IReadOnlyList<TestResult> results)
    {
        var oldEvidence = new List<string>();
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        await using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT evidence_path FROM test_results WHERE run_id=$r AND source=$s AND evidence_path IS NOT NULL";
            sel.Parameters.AddWithValue("$r", runId.ToString());
            sel.Parameters.AddWithValue("$s", source);
            await using var rd = await sel.ExecuteReaderAsync();
            while (await rd.ReadAsync()) oldEvidence.Add(rd.GetString(0));
        }

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM test_results WHERE run_id=$r AND source=$s";
            del.Parameters.AddWithValue("$r", runId.ToString());
            del.Parameters.AddWithValue("$s", source);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var r in results)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO test_results
(id,run_id,source,category,name,status,severity,started_at,duration_ms,url,http_status,message,evidence_path,json)
VALUES($id,$run,$src,$cat,$name,$status,$sev,$started,$ms,$url,$http,$msg,$ev,$json)";
            ins.Parameters.AddWithValue("$id", r.Id.ToString());
            ins.Parameters.AddWithValue("$run", runId.ToString());
            ins.Parameters.AddWithValue("$src", source);
            ins.Parameters.AddWithValue("$cat", Db(r.Category));
            ins.Parameters.AddWithValue("$name", Db(r.Name));
            ins.Parameters.AddWithValue("$status", r.Status.ToString());
            ins.Parameters.AddWithValue("$sev", r.Severity.ToString());
            ins.Parameters.AddWithValue("$started", r.StartedAt.ToString("o", CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("$ms", (long)r.Duration.TotalMilliseconds);
            ins.Parameters.AddWithValue("$url", Db(r.Url));
            ins.Parameters.AddWithValue("$http", Db(r.HttpStatus));
            ins.Parameters.AddWithValue("$msg", Db(r.Message));
            ins.Parameters.AddWithValue("$ev", Db(r.EvidencePath));
            ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(r, JsonDefaults.Options));
            await ins.ExecuteNonQueryAsync();
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = @"UPDATE test_runs SET
 total    =(SELECT COUNT(*) FROM test_results WHERE run_id=$r),
 passed   =(SELECT COUNT(*) FROM test_results WHERE run_id=$r AND status='Pass'),
 failed   =(SELECT COUNT(*) FROM test_results WHERE run_id=$r AND status='Fail'),
 warnings =(SELECT COUNT(*) FROM test_results WHERE run_id=$r AND status='Warning'),
 skipped  =(SELECT COUNT(*) FROM test_results WHERE run_id=$r AND status='Skipped'),
 blocked  =(SELECT COUNT(*) FROM test_results WHERE run_id=$r AND status='Blocked'),
 updated_at=$now
WHERE id=$r";
            upd.Parameters.AddWithValue("$r", runId.ToString());
            upd.Parameters.AddWithValue("$now", Now());
            await upd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return oldEvidence;
    }

    private const string RunColumns =
        "id,project_name,project_path,started_at,updated_at,total,passed,failed,warnings,skipped,blocked";

    private static RunSummary MapRun(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
        DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10));

    public async Task<IReadOnlyList<RunSummary>> GetRunsAsync(int limit = 100)
    {
        var list = new List<RunSummary>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {RunColumns} FROM test_runs ORDER BY started_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) list.Add(MapRun(rd));
        return list;
    }

    public async Task<RunSummary?> GetRunAsync(Guid runId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {RunColumns} FROM test_runs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", runId.ToString());
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? MapRun(rd) : null;
    }

    public async Task<IReadOnlyList<TestResult>> GetResultsAsync(Guid runId)
    {
        var list = new List<TestResult>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM test_results WHERE run_id=$r ORDER BY rowid";
        cmd.Parameters.AddWithValue("$r", runId.ToString());
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var r = JsonSerializer.Deserialize<TestResult>(rd.GetString(0), JsonDefaults.Options);
            if (r is not null) list.Add(r);
        }
        return list;
    }
}