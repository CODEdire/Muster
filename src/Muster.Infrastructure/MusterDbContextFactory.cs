using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Muster.Infrastructure;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the model without the Aspire host.
/// The connection string is never used at design time (no database is contacted).
/// </summary>
public class MusterDbContextFactory : IDesignTimeDbContextFactory<MusterDbContext>
{
    public MusterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MusterDbContext>()
            .UseSqlServer("Server=(localdb);Database=Muster;Trusted_Connection=True;")
            .Options;

        return new MusterDbContext(options);
    }
}
