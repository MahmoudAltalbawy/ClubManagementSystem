using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SportsPlatform.Auth.Infrastructure.Data;

/// <summary>
/// Runs numbered SQL migration scripts from an embedded or file-based source.
/// Tracks applied migrations in a <c>_applied_sql_migrations</c> table.
/// 
/// Naming convention: NNN_description.sql (e.g., 001_refresh_token_fix.sql)
/// Scripts run in lexicographic order. Each script runs exactly once.
/// </summary>
public class SqlMigrationRunner
{
    private readonly AppDbContext _db;
    private readonly ILogger<SqlMigrationRunner> _logger;

    public SqlMigrationRunner(AppDbContext db, ILogger<SqlMigrationRunner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the tracking table exists, then applies any pending SQL scripts
    /// from the given directory in lexicographic order.
    /// </summary>
    public async Task RunPendingMigrationsAsync(string scriptsDirectory)
    {
        await EnsureTrackingTableAsync();

        if (!Directory.Exists(scriptsDirectory))
        {
            _logger.LogWarning("SQL migrations directory not found: {Dir}. Skipping.", scriptsDirectory);
            return;
        }

        var appliedMigrations = await GetAppliedMigrationsAsync();

        var scriptFiles = Directory.GetFiles(scriptsDirectory, "*.sql")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var pending = scriptFiles
            .Where(f => !appliedMigrations.Contains(f))
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("No pending SQL migrations.");
            return;
        }

        _logger.LogInformation("Found {Count} pending SQL migration(s).", pending.Count);

        foreach (var scriptName in pending)
        {
            var scriptPath = Path.Combine(scriptsDirectory, scriptName);
            var sql = await File.ReadAllTextAsync(scriptPath);

            _logger.LogInformation("Applying SQL migration: {Script}...", scriptName);

            await using var transaction = await _db.Database.BeginTransactionAsync();

            try
            {
                await _db.Database.ExecuteSqlRawAsync(sql);
                await RecordMigrationAsync(scriptName);
                await transaction.CommitAsync();

                _logger.LogInformation("Applied SQL migration: {Script}", scriptName);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to apply SQL migration: {Script}. Rolling back.", scriptName);
                throw;
            }
        }
    }

    private async Task EnsureTrackingTableAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS public._applied_sql_migrations (
                script_name TEXT PRIMARY KEY,
                applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
        ");
    }

    private async Task<HashSet<string>> GetAppliedMigrationsAsync()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync();

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT script_name FROM public._applied_sql_migrations";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(reader.GetString(0));
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }

        return result;
    }

    private async Task RecordMigrationAsync(string scriptName)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO public._applied_sql_migrations (script_name) VALUES ({scriptName})");
    }
}
