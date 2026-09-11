using System.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace PropSeekr.Configuration;

public sealed class DatabaseReadinessHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Unhealthy("Database connection is not configured.");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = Math.Min(new NpgsqlConnectionStringBuilder(connectionString).Timeout, 5),
                CommandTimeout = 5
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = """
                SELECT
                    to_regprocedure('public.sp_run_matching_engine(integer,integer)') IS NOT NULL
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'listings' AND column_name = 'content_version')
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'requirements' AND column_name = 'content_version')
                    AND EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'embedding_jobs' AND column_name = 'target_version');
                """;
            var compatible = (bool?)await command.ExecuteScalarAsync(cancellationToken) == true;
            return compatible
                ? HealthCheckResult.Healthy("Database and required matching schema are ready.")
                : HealthCheckResult.Unhealthy("Database is reachable but required schema or matching procedure is missing.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database readiness check failed.", ex);
        }
    }
}
