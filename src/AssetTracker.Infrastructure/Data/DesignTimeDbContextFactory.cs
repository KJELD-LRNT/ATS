using AssetTracker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AssetTracker.Infrastructure.Data;

/// <summary>Used only by `dotnet ef` to generate SQL Server migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\design;Database=AssetTracker;Trusted_Connection=True",
                o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;
        return new AppDbContext(options);
    }
}
