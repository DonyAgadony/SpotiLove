using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Spotilove;

public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();

        var connectionString =
            Environment.GetEnvironmentVariable("DatabaseURL")
            ?? "Data Source=spotilove.db";

        if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
        {
            var databaseUri = new Uri(connectionString);
            var userInfo = databaseUri.UserInfo.Split(':', 2);

            var connStrBuilder = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = databaseUri.Host,
                Port = databaseUri.Port > 0 ? databaseUri.Port : 5432,
                Database = databaseUri.LocalPath.TrimStart('/').Split('?')[0],
                Username = userInfo[0],
                Password = userInfo[1],
                SslMode = Npgsql.SslMode.Require,
                TrustServerCertificate = true
            };

            builder.UseNpgsql(connStrBuilder.ConnectionString)
                   .UseSnakeCaseNamingConvention();
        }
        else
        {
            builder.UseSqlite(connectionString);
        }

        return new AppDbContext(builder.Options);
    }
}
