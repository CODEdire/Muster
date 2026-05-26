using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;

namespace Muster.Persistence.UnitTests;

/// <summary>
/// A throwaway SQLite in-memory database for a single test. The connection is held open for the lifetime
/// of the instance (an in-memory SQLite database vanishes when its last connection closes), and the schema
/// is created from the EF model via EnsureCreated — so real relational behaviour (constraints, unique and
/// filtered indexes, transactions, JSON columns) is exercised, unlike the EF in-memory provider.
/// </summary>
internal sealed class SqliteDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Context = new MusterDbContext(
            new DbContextOptionsBuilder<MusterDbContext>().UseSqlite(_connection).Options);
        Context.Database.EnsureCreated();
    }

    public MusterDbContext Context { get; }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
