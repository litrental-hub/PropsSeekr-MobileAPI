using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PropSeekr.Data;

namespace PropSeekr.Services;

/// <summary>
/// Resolves a submitted locality to the canonical master catalogue. Callers must
/// hold a database transaction so catalogue creation and inventory creation are atomic.
/// </summary>
public static class MasterLocationResolver
{
    public static async Task<int> ResolveAsync(
        AppDbContext dbContext,
        string city,
        string locality,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(locality))
            throw new ArgumentException("City and locality are required for nearby search.");
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentException("Valid property coordinates are required for nearby search.");

        var currentTransaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A database transaction is required to resolve a master location.");
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = currentTransaction.GetDbTransaction();
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtext('propseekr-master-location'));";
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = currentTransaction.GetDbTransaction();
            existingCommand.CommandText = """
                UPDATE public.master
                SET lat = COALESCE(lat, @lat),
                    lng = COALESCE(lng, @lng),
                    city = COALESCE(NULLIF(city, ''), @city),
                    area = COALESCE(NULLIF(area, ''), @locality),
                    geocoding_status = 'verified',
                    geocoding_provider = 'user',
                    location_precision = 'USER_SELECTED',
                    geocoding_confidence = 1.0,
                    geocoded_at = NOW(),
                    geocoding_error = NULL,
                    review_required = FALSE
                WHERE masterid = (
                    SELECT masterid
                    FROM public.master
                    WHERE LOWER(TRIM(area)) = LOWER(TRIM(@locality))
                      AND LOWER(TRIM(COALESCE(city, ''))) = LOWER(TRIM(@city))
                    ORDER BY masterid
                    LIMIT 1
                )
                RETURNING masterid;
                """;
            AddParameter(existingCommand, "@city", city.Trim());
            AddParameter(existingCommand, "@locality", locality.Trim());
            AddParameter(existingCommand, "@lat", latitude);
            AddParameter(existingCommand, "@lng", longitude);
            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null and not DBNull)
                return Convert.ToInt32(existing);
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = currentTransaction.GetDbTransaction();
        insertCommand.CommandText = """
            INSERT INTO public.master (
                area, city, lat, lng, geocoding_status, geocoding_provider,
                location_precision, geocoding_confidence, geocoded_at, review_required)
            VALUES (
                @locality, @city, @lat, @lng, 'verified', 'user',
                'USER_SELECTED', 1.0, NOW(), FALSE)
            RETURNING masterid;
            """;
        AddParameter(insertCommand, "@city", city.Trim());
        AddParameter(insertCommand, "@locality", locality.Trim());
        AddParameter(insertCommand, "@lat", latitude);
        AddParameter(insertCommand, "@lng", longitude);
        return Convert.ToInt32(await insertCommand.ExecuteScalarAsync(cancellationToken));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
