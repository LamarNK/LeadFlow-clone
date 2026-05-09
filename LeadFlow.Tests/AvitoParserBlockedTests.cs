using LeadFlow.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>
/// Тесты вкладки «С ошибками» (<c>tab(rejected)</c>) — снимок HTML взят из реального ответа Авито,
/// где одна заблокированная вакансия с подписью «Заблокировано, удалится навсегда 22 мая в 20:50».
/// </summary>
public sealed class AvitoParserBlockedTests
{
    private readonly AvitoParserService _parser = new();

    [Fact]
    public void ParseBlockedTabPage_SingleRejectedJob_ReturnsOneAdWithStatusAndDeleteDate()
    {
        // Минимально-достаточная разметка вкладки «С ошибками»: одна вакансия в /rabota/-категории,
        // блок статуса со span class=styles-status-name_red-* и хвостом «, удалится навсегда ...».
        const string html = """
            <div class="style-scrollable-content-container-iZOic">
              <div role-marker="offer">
                <div class="style-layout-b2g0K" data-marker="item-snippet/8095860333">
                  <div class="styles-snippet-IdzHk">
                    <a data-marker="view-link" href="//www.avito.ru/solikamsk/vakansii/raznorabochiy_vahta_8095860333">
                      <span class="styles-title-UJzSB">Разнорабочий вахта</span>
                    </a>
                    <span class="styles-address-I7r1Q">Пермский край, Соликамск</span>
                  </div>
                  <div class="styles-status-wrapper-Jlm37">
                    <span class="styles-status-EGgGM">
                      <div class="styles-catch-block-EZLET">
                        <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
                      </div>, удалится навсегда 22 мая в 20:50
                    </span>
                  </div>
                </div>
              </div>
            </div>
            """;

        var blocked = _parser.ParseBlockedTabPage(html);

        var ad = Assert.Single(blocked);
        Assert.Equal("8095860333", ad.Id);
        Assert.Equal("Разнорабочий вахта", ad.Title);
        Assert.Equal("Пермский край, Соликамск", ad.City);
        Assert.Equal("Заблокировано", ad.Status);
        Assert.Equal("удалится навсегда 22 мая в 20:50", ad.DeleteDate);
    }

    [Fact]
    public void ParseBlockedTabPage_NonJobListing_IsSkipped()
    {
        // Снимок не из раздела /rabota/ — фильтр по «вакансиям» отбрасывает.
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/777">
                <a data-marker="view-link" href="//www.avito.ru/moscow/audio_video/televizor_777">
                  <span class="styles-title-UJzSB">Телевизор</span>
                </a>
                <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
              </div>
            </div>
            """;

        var blocked = _parser.ParseBlockedTabPage(html);

        Assert.Empty(blocked);
    }

    [Fact]
    public void ParseBlockedTabPage_MissingDeleteDate_StatusStillExtracted()
    {
        // Иногда «удалится навсегда» отсутствует — сам статус всё равно должен подняться.
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/1">
                <a data-marker="view-link" href="//www.avito.ru/perm/vakansii/test_1">
                  <span class="styles-title-UJzSB">Тест</span>
                </a>
                <div class="styles-status-wrapper-Jlm37">
                  <span class="styles-status-EGgGM">
                    <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
                  </span>
                </div>
              </div>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseBlockedTabPage(html));
        Assert.Equal("Заблокировано", ad.Status);
        Assert.Equal(string.Empty, ad.DeleteDate);
    }

    [Fact]
    public void ParseBlockedTabPage_EmptyOrNullHtml_ReturnsEmpty()
    {
        Assert.Empty(_parser.ParseBlockedTabPage(string.Empty));
        Assert.Empty(_parser.ParseBlockedTabPage(null!));
    }

    [Fact]
    public void ParseProfilePage_BlockedSnippetOnActiveTab_StillSkipped()
    {
        // Если кейс «осталась заблокированная карточка на активной вкладке» — её
        // в ActiveAds НЕ добавляем (это работа ParseBlockedTabPage).
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/123">
                <a data-marker="view-link" href="//www.avito.ru/perm/vakansii/test_123">
                  <span class="styles-title-UJzSB">Тест активной</span>
                </a>
                <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
              </div>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.Empty(result.ActiveAds);
        Assert.Empty(result.BlockedAds);
    }
}
