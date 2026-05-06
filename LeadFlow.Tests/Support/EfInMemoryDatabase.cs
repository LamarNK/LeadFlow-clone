using LeadFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Tests.Support;

/// <summary>EF Core in-memory store per instance (avoids SQLite/sqlcipher native conflicts with the main app).</summary>
internal sealed class EfInMemoryDatabase
{
    public EfInMemoryDatabase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Factory = new TestAppDbContextFactory(options);
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
    }

    public IDbContextFactory<AppDbContext> Factory { get; }
}

internal sealed class TestAppDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
