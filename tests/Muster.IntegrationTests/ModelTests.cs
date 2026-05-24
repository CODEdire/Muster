using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure;
using Xunit;

namespace Muster.IntegrationTests;

public class ModelTests
{
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
