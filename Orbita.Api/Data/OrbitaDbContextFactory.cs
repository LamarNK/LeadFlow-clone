using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Orbita.Api.Data;

/// <summary>Lets EF tools build migrations without constructing runtime singleton services.</summary>
public sealed class OrbitaDbContextFactory : IDesignTimeDbContextFactory<OrbitaDbContext>
{
    public OrbitaDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Host=localhost;Database=orbita;Username=postgres;Password=postgres";
        return new OrbitaDbContext(new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql(connectionString)
            .Options);
    }
}
