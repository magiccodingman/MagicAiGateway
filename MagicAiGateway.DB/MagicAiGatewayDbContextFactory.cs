using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MagicAiGateway.DB;

public sealed class MagicAiGatewayDbContextFactory : IDesignTimeDbContextFactory<MagicAiGatewayDbContext>
{
    public MagicAiGatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MAGICAI_DB_CONNECTION_STRING")
                               ?? throw new InvalidOperationException(
                                   "Set MAGICAI_DB_CONNECTION_STRING before running EF Core design-time commands.");
        var builder = new DbContextOptionsBuilder<MagicAiGatewayDbContext>();
        builder.UseNpgsql(connectionString, options =>
            options.MigrationsAssembly(typeof(MagicAiGatewayDbContext).Assembly.FullName));
        return new MagicAiGatewayDbContext(builder.Options);
    }
}
