using Npgsql;

namespace PropSeekr.Data;

public static class ConnectionStringHelper
{
    public static string Build(IConfiguration configuration)
    {
        var host = Environment.GetEnvironmentVariable("DB_HOST")?.Trim();
        var baseConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(baseConnectionString) && string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection or DB_HOST for the database connection.");
        }

        var builder = string.IsNullOrWhiteSpace(baseConnectionString)
            ? new NpgsqlConnectionStringBuilder()
            : new NpgsqlConnectionStringBuilder(baseConnectionString);

        if (!string.IsNullOrWhiteSpace(host))
        {
            builder.Host = host;
        }

        var sslMode = Environment.GetEnvironmentVariable("DB_SSLMODE");
        if (!string.IsNullOrWhiteSpace(sslMode) &&
            Enum.TryParse<SslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
        {
            builder.SslMode = parsedSslMode;
        }

        var port = Environment.GetEnvironmentVariable("DB_PORT");
        if (int.TryParse(port, out var parsedPort))
        {
            builder.Port = parsedPort;
        }

        var database = Environment.GetEnvironmentVariable("DB_NAME");
        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database;
        }

        var username = Environment.GetEnvironmentVariable("DB_USERNAME")?.Trim()
                       ?? Environment.GetEnvironmentVariable("DB_USER")?.Trim();
        if (!string.IsNullOrWhiteSpace(username))
        {
            builder.Username = username;
        }

        var password = Environment.GetEnvironmentVariable("DB_PASSWORD");
        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }
}
