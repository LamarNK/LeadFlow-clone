using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerAdminServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task RenameAsync_OperatorFromWorkerOffice_ChangesName()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Новое имя", OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.Equal("Новое имя", worker!.DisplayName);
        Assert.Equal("Новое имя", (await db.Workers.SingleAsync(x => x.Id == WorkerA)).DisplayName);
    }

    [Fact]
    public async Task RenameAsync_OperatorFromAnotherOffice_ReturnsNotFoundAndKeepsName()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Чужое имя", OfficeScope.ForOffice(OfficeB));

        Assert.Null(worker);
        Assert.Equal("Воркер не найден.", error);
        Assert.Equal("Воркер A", (await db.Workers.SingleAsync(x => x.Id == WorkerA)).DisplayName);
    }

    [Fact]
    public async Task RenameAsync_GlobalAdmin_ChangesNameInAnyOffice()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Имя администратора", OfficeScope.GlobalAdmin);

        Assert.Null(error);
        Assert.Equal("Имя администратора", worker!.DisplayName);
    }

    private static WorkerAdminService CreateService(OrbitaDbContext db) =>
        new(
            db,
            new ConfigurationBuilder().Build(),
            null!,
            null!,
            new WorkerConnectionRegistry(),
            null!,
            null!);

    private static async Task SeedAsync(OrbitaDbContext db)
    {
        db.Offices.AddRange(
            new OfficeEntity { Id = OfficeA, Name = "Офис A", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = OfficeB, Name = "Офис B", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerA,
            OfficeId = OfficeA,
            DisplayName = "Воркер A",
            MachineName = "host-a",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static OrbitaDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
