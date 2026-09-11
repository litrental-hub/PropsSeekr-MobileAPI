using Npgsql;

namespace PropSeekr.Data;

public static class ConnectionStringHelper
{
    public static string Build(IConfiguration configuration)
    {
        var baseConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
            throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection for the database connection.");

        return new NpgsqlConnectionStringBuilder(baseConnectionString).ConnectionString;
    }
}
