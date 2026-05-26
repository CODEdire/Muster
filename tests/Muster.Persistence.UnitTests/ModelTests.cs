using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Xunit;

namespace Muster.Persistence.UnitTests;

public class ModelTests
{
    /// <summary>Builds the model under the real SQL Server provider (no DB contacted) so SQL-Server-specific
    /// mappings — rowversion, decimal-mapped ulong keys, JSON owned types, filtered indexes — are validated.</summary>
    [Fact]
    public void ModelBuildsWithoutErrors()
    {
        var options = new DbContextOptionsBuilder<MusterDbContext>()
            .UseSqlServer("Server=localhost;Database=muster;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var db = new MusterDbContext(options);

        // Forces EF to build the model; throws if a mapping is invalid.
        Assert.NotNull(db.Model);
    }
}
