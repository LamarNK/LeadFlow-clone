using System.Text.Json;
using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Orbita.Tests;

public sealed class AppRepositoryMultiloginTests
{
    [Fact]
    public async Task SaveAccount_RoundTripsMultiloginFields_WithoutTouchingAdsPower()
    {
        using var db = new AppDbHarness();
        var repository = new AppRepository(db.Factory);
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "MLX acc",
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "11111111-2222-3333-4444-555555555555",
            MultiloginProfileName = "Avito Pro 1",
            MultiloginFolderId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            MultiloginLauncherUrl = "https://launcher.mlx.yt:45001",
            MultiloginCloudApiUrl = "https://api.multilogin.com",
            MultiloginAutomationToken = "secret-token"
        };

        await repository.SaveAccountAsync(account, CancellationToken.None);
        var loaded = (await repository.GetAccountsAsync(CancellationToken.None)).Single();

        Assert.Equal(AvitoProfileProvider.Multilogin, loaded.ProfileProvider);
        Assert.Equal(account.MultiloginProfileId, loaded.MultiloginProfileId);
        Assert.Equal(account.MultiloginProfileName, loaded.MultiloginProfileName);
        Assert.Equal(account.MultiloginFolderId, loaded.MultiloginFolderId);
        Assert.Equal(account.MultiloginLauncherUrl, loaded.MultiloginLauncherUrl);
        Assert.Equal(account.MultiloginCloudApiUrl, loaded.MultiloginCloudApiUrl);
        Assert.Equal(account.MultiloginAutomationToken, loaded.MultiloginAutomationToken);
        Assert.Null(loaded.AdsPowerProfileId);
        Assert.Null(loaded.AdsPowerApiBaseUrl);
        Assert.Null(loaded.AdsPowerApiKey);
    }

    [Fact]
    public async Task SaveAccount_PreservesAdsPowerMapping()
    {
        using var db = new AppDbHarness();
        var repository = new AppRepository(db.Factory);
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "AdsPower acc",
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = "user-42",
            AdsPowerProfileName = "Profile 42",
            AdsPowerApiBaseUrl = "http://local.adspower.net:50325",
            AdsPowerApiKey = "ads-key"
        };

        await repository.SaveAccountAsync(account, CancellationToken.None);
        var loaded = (await repository.GetAccountsAsync(CancellationToken.None)).Single();

        Assert.Equal(AvitoProfileProvider.AdsPower, loaded.ProfileProvider);
        Assert.Equal("user-42", loaded.AdsPowerProfileId);
        Assert.Equal("Profile 42", loaded.AdsPowerProfileName);
        Assert.Equal("http://local.adspower.net:50325", loaded.AdsPowerApiBaseUrl);
        Assert.Equal("ads-key", loaded.AdsPowerApiKey);
        Assert.Null(loaded.MultiloginProfileId);
        Assert.Null(loaded.MultiloginAutomationToken);
    }

    [Fact]
    public async Task GetAccounts_UnknownStoredProvider_DefaultsToLocal()
    {
        using var db = new AppDbHarness();
        var id = Guid.NewGuid();
        await using (var context = await db.Factory.CreateDbContextAsync())
        {
            context.AvitoAccounts.Add(new AvitoAccountEntity
            {
                Id = id,
                DisplayName = "legacy",
                ProfileProvider = "NotAProvider"
            });
            await context.SaveChangesAsync();
        }

        var repository = new AppRepository(db.Factory);
        var loaded = (await repository.GetAccountsAsync(CancellationToken.None)).Single();
        Assert.Equal(AvitoProfileProvider.Local, loaded.ProfileProvider);
    }

    [Fact]
    public async Task GetAccountsForSettings_RoundTripsMultiloginFields()
    {
        using var db = new AppDbHarness();
        var repository = new AppRepository(db.Factory);
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "settings",
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "pid",
            MultiloginFolderId = "fid",
            MultiloginAutomationToken = "tok"
        };
        await repository.SaveAccountAsync(account, CancellationToken.None);

        var listed = (await repository.GetAccountsForSettingsAsync(CancellationToken.None)).Single();
        Assert.Equal(AvitoProfileProvider.Multilogin, listed.ProfileProvider);
        Assert.Equal("pid", listed.MultiloginProfileId);
        Assert.Equal("fid", listed.MultiloginFolderId);
        Assert.Equal("tok", listed.MultiloginAutomationToken);
    }

    [Fact]
    public void JsonRoundTrip_KeepsProviderAndMultiloginFields()
    {
        var account = new AvitoAccount
        {
            DisplayName = "json",
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "pid",
            MultiloginProfileName = "name",
            MultiloginFolderId = "fid",
            MultiloginLauncherUrl = "https://launcher.mlx.yt:45001",
            MultiloginCloudApiUrl = "https://api.multilogin.com/",
            MultiloginAutomationToken = "tok",
            AdsPowerProfileId = "should-stay"
        };

        var json = JsonSerializer.Serialize(account);
        var loaded = JsonSerializer.Deserialize<AvitoAccount>(json);
        Assert.NotNull(loaded);
        Assert.Equal(AvitoProfileProvider.Multilogin, loaded.ProfileProvider);
        Assert.Equal("pid", loaded.MultiloginProfileId);
        Assert.Equal("name", loaded.MultiloginProfileName);
        Assert.Equal("fid", loaded.MultiloginFolderId);
        Assert.Equal("https://launcher.mlx.yt:45001", loaded.MultiloginLauncherUrl);
        Assert.Equal("https://api.multilogin.com/", loaded.MultiloginCloudApiUrl);
        Assert.Equal("tok", loaded.MultiloginAutomationToken);
        Assert.Equal("should-stay", loaded.AdsPowerProfileId);
    }

    [Fact]
    public void JsonDeserialize_MissingProvider_DefaultsToLocal()
    {
        const string json = """{"DisplayName":"old-account"}""";
        var loaded = JsonSerializer.Deserialize<AvitoAccount>(json);
        Assert.NotNull(loaded);
        Assert.Equal(AvitoProfileProvider.Local, loaded.ProfileProvider);
        Assert.Null(loaded.MultiloginProfileId);
        Assert.Null(loaded.AdsPowerProfileId);
    }

    [Fact]
    public void JsonDeserialize_NumericAdsPowerProvider_RemainsAdsPower()
    {
        const string json = """{"DisplayName":"old-adspower","ProfileProvider":1}""";
        var loaded = JsonSerializer.Deserialize<AvitoAccount>(json);
        Assert.NotNull(loaded);
        Assert.Equal(AvitoProfileProvider.AdsPower, loaded.ProfileProvider);
        Assert.Null(loaded.MultiloginProfileId);
    }

    [Fact]
    public async Task SqliteSchema_AddsNullableMultiloginColumns_OnLegacyDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new AppDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var repository = new AppRepository(new AppDbContextFactory(options));
        await repository.InitializeAsync(new AppSettings(), CancellationToken.None);
        await AssertMultiloginColumnsAsync(connection);

        foreach (var column in MultiloginColumns)
        {
            await using var drop = new SqliteCommand($"ALTER TABLE AvitoAccounts DROP COLUMN {column};", connection);
            await drop.ExecuteNonQueryAsync();
        }

        var remaining = await ReadColumnsAsync(connection);
        Assert.DoesNotContain("MultiloginProfileId", remaining.Keys);
        Assert.Contains("AdsPowerProfileId", remaining.Keys);

        await repository.InitializeAsync(new AppSettings(), CancellationToken.None);
        await AssertMultiloginColumnsAsync(connection);
    }

    private static readonly string[] MultiloginColumns =
    [
        "MultiloginProfileId",
        "MultiloginProfileName",
        "MultiloginFolderId",
        "MultiloginLauncherUrl",
        "MultiloginCloudApiUrl",
        "MultiloginAutomationToken"
    ];

    private static async Task AssertMultiloginColumnsAsync(SqliteConnection connection)
    {
        var columns = await ReadColumnsAsync(connection);
        Assert.Contains("AdsPowerProfileId", columns.Keys);
        foreach (var column in MultiloginColumns)
        {
            Assert.Equal("TEXT", columns[column]);
        }
    }

    private static async Task<Dictionary<string, string>> ReadColumnsAsync(SqliteConnection connection)
    {
        await using var check = new SqliteCommand("PRAGMA table_info(AvitoAccounts);", connection);
        await using var reader = await check.ExecuteReaderAsync();
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(1)] = reader.GetString(2);
        }

        return columns;
    }

    private sealed class AppDbHarness : IDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public AppDbHarness()
        {
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Factory = new AppDbContextFactory(_options);
            using var db = new AppDbContext(_options);
            db.Database.EnsureCreated();
        }

        public IDbContextFactory<AppDbContext> Factory { get; }

        public void Dispose()
        {
        }
    }

    private sealed class AppDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
