using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Tests;

public sealed class AccountRowSnapshotJsonTests
{
    [Fact]
    public void SnapshotJson_IncludesSubProfileIdAndName()
    {
        var accountId = Guid.Parse("3b386125-8751-6cb2-7c9f-a2cfa238d7a6");
        var row = new AccountRowViewModel
        {
            Id = accountId,
            AccountName = "Avito 1",
            WorkerId = Guid.Parse("c097326e-230b-4b7b-b80c-635eb5b4d567"),
            WorkerName = "WM1",
            SubProfiles =
            [
                new SubProfileRowViewModel
                {
                    Id = "12345",
                    Name = "Служба России 3",
                    StatusLabel = "Активен",
                    StatusTone = "success",
                    BalanceText = "1 000 ₽"
                }
            ]
        };

        var jsonResult = new JsonResult(new AccountsLiveSnapshotViewModel
        {
            UpdatedAtUtc = DateTime.UtcNow,
            Accounts = [row]
        });

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(jsonResult.Value, options);
        using var doc = JsonDocument.Parse(json);
        var sub = doc.RootElement.GetProperty("accounts")[0].GetProperty("subProfiles")[0];

        Assert.Equal("12345", sub.GetProperty("id").GetString());
        Assert.Equal("Служба России 3", sub.GetProperty("name").GetString());
        Assert.Equal("Активен", sub.GetProperty("statusLabel").GetString());
    }
}