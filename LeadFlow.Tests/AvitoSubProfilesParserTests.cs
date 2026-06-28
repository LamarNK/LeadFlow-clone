using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoSubProfilesParserTests
{
    [Fact]
    public void Parse_TwoCardsWithCurrentMarker_ReturnsBothInOrder()
    {
        const string html = """
            <div>
              <div role="button" data-marker="component-profile-switch/profile-434877613" class="ProfileCard-module-root-WwzX1 ProfileCard-module-isCurrent-MZkOj">
                <div>
                  <h5 class="...">Служба России 3</h5>
                  <p class="...">Работа</p>
                </div>
              </div>
              <div role="button" data-marker="component-profile-switch/profile-434874096" class="ProfileCard-module-root-WwzX1">
                <div>
                  <h5 class="...">Служба России 2</h5>
                  <p class="...">Работа</p>
                </div>
              </div>
              <a data-marker="component-profile-switch/add" href="#profile/add">Добавить профиль</a>
            </div>
            """;

        var profiles = AvitoSubProfilesParser.Parse(html);

        Assert.Equal(2, profiles.Count);

        Assert.Equal("434877613", profiles[0].Id);
        Assert.Equal("Служба России 3", profiles[0].Name);
        Assert.Equal("Работа", profiles[0].Category);
        Assert.True(profiles[0].IsCurrent);

        Assert.Equal("434874096", profiles[1].Id);
        Assert.Equal("Служба России 2", profiles[1].Name);
        Assert.Equal("Работа", profiles[1].Category);
        Assert.False(profiles[1].IsCurrent);
    }

    [Fact]
    public void Parse_EmptyOrNullHtml_ReturnsEmpty()
    {
        Assert.Empty(AvitoSubProfilesParser.Parse(null));
        Assert.Empty(AvitoSubProfilesParser.Parse(""));
        Assert.Empty(AvitoSubProfilesParser.Parse("<html><body>no profiles</body></html>"));
    }

    [Fact]
    public void Parse_DuplicateIds_DeduplicatesByIdKeepingFirst()
    {
        const string html = """
            <div data-marker="component-profile-switch/profile-100">
              <h5>First</h5><p>Cat1</p>
            </div>
            <div data-marker="component-profile-switch/profile-100">
              <h5>Second</h5><p>Cat2</p>
            </div>
            """;

        var profiles = AvitoSubProfilesParser.Parse(html);

        Assert.Single(profiles);
        Assert.Equal("100", profiles[0].Id);
        Assert.Equal("First", profiles[0].Name);
    }

    [Fact]
    public void Parse_DecodesCommonHtmlEntities()
    {
        const string html = """
            <div data-marker="component-profile-switch/profile-1">
              <h5>Иванов &amp; Co</h5><p>Услуги</p>
            </div>
            """;

        var profiles = AvitoSubProfilesParser.Parse(html);

        Assert.Single(profiles);
        Assert.Equal("Иванов & Co", profiles[0].Name);
    }
}
