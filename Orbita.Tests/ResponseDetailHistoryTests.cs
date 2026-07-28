using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class ResponseDetailHistoryTests
{
    [Fact]
    public void MapDetailJson_WhenPhoneChanged_ShowsPhoneHistoryInCard()
    {
        var changedAt = new DateTime(2026, 7, 27, 19, 5, 23, DateTimeKind.Utc);
        var detail = new ResponseDetailViewModel
        {
            PhoneRaw = "+7 922 234-68-20",
            PhoneNormalized = "79222346820",
            PreviousPhoneRaw = "+7 901 946-47-49",
            PreviousPhoneNormalized = "79019464749",
            CreatedAtUtc = changedAt,
            CollectedAtUtc = changedAt.AddMinutes(5),
            PhoneHistory =
            [
                new CandidatePhoneHistoryDto("+7 901 946-47-49", "79019464749", changedAt.AddDays(-1)),
                new CandidatePhoneHistoryDto("+7 922 234-68-20", "79222346820", changedAt)
            ]
        };

        var card = ResponsesIndexBuilder.MapDetailJson(detail);

        var history = Assert.Single(card.Sections, x => x.Label == "История номеров");
        Assert.Equal(2, history.Values.Count);
        Assert.Contains("+7 901 946-47-49", history.Values[0]);
        Assert.Contains("+7 922 234-68-20", history.Values[1]);
    }
}
