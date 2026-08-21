using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>
/// Покрывает три самых частых случая блокировки/капчи Avito + страховку от ложных срабатываний
/// на «обычных» страницах кабинета.
/// </summary>
public sealed class AvitoCaptchaDetectorTests
{
    [Fact]
    public void IsCaptchaHtml_FirewallContainerWithHCaptcha_DetectedAsHCaptcha()
    {
        // Реальная разметка firewall-страницы Avito: «Доступ ограничен: проблема с IP»
        // + встроенный hCaptcha + image-captcha + geetest. Любого из этих маркеров достаточно.
        const string html = """
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <form class="form js-submit js-firewall-form" autocomplete="off">
                <div id="h-captcha" style="display: none">
                  <div class="h-captcha" data-sitekey="070db171-ddb9-4c93-b7f6-d25d3c9d7e28"
                       data-hcaptcha-widget-id="06cn1ag3l2l6"></div>
                </div>
                <fieldset id="inner-captcha" class="js-form-captcha">
                  <input id="form-input" name="captcha">
                </fieldset>
                <div id="geetest_captcha"></div>
              </form>
            </div>
            """;

        Assert.True(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Equal("firewall", AvitoCaptchaDetector.Classify(html));
        Assert.True(AvitoCaptchaDetector.HasGeeTestWidget(html));
    }

    [Fact]
    public void Classify_GeetestOnly_ReturnsGeetest()
    {
        const string html = """
            <div id="geetest_captcha" style="display: inline-block;">
              <p class="text">Нажмите на кнопку Продолжить для решения капчи</p>
            </div>
            """;

        Assert.Equal("geetest", AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void Classify_ImageCaptchaOnly_ReturnsImageCaptcha()
    {
        const string html = """
            <fieldset id="inner-captcha" class="js-form-captcha form-fieldset form-captcha">
              <input id="form-input" type="text" class="form-captcha-input js-captcha-input" name="captcha">
              <img class="form-captcha-image js-form-captcha-image">
            </fieldset>
            """;

        Assert.Equal("image-captcha", AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void Classify_FirewallTitleOnly_ReturnsFirewall()
    {
        // Минимальный шаблон: заголовок и текст «Доступ ограничен», без явных hCaptcha.
        const string html = """
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
            </div>
            """;

        Assert.Equal("firewall", AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void IsCaptchaHtml_NormalProfileItemsPage_NotDetected()
    {
        // Страховка от ложных срабатываний на обычной вкладке «Активные» —
        // такой HTML не должен ловиться даже если в подсказках/тексте мелькнёт «капча».
        const string html = """
            <div class="style-list-container-SP3qU">
              <div data-marker="profile-items-tab/tab(active)">Активные</div>
              <div data-marker="item-snippet/12345">Объявление</div>
            </div>
            """;

        Assert.False(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Null(AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void IsCaptchaHtml_NormalSwitchModal_NotDetected()
    {
        // Тот же тест для модалки переключения профилей: data-marker=component-profile-switch — нормальный маркер.
        const string html = """
            <div data-marker="component-profile-switch/profile-434877613">
              <h5>Служба России</h5>
            </div>
            """;

        Assert.False(AvitoCaptchaDetector.IsCaptchaHtml(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCaptchaHtml_NullOrEmpty_ReturnsFalse(string? html)
    {
        Assert.False(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Null(AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void IsCaptchaHtml_BareTextDostupOgranichen_DetectedAsFirewall()
    {
        // Если Avito отдал минимальный HTML без классов, но оставил видимый текст —
        // текстовый маркер должен сработать.
        const string html = "<html><body><h2>Доступ ограничен: проблема с IP</h2></body></html>";

        Assert.True(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Equal("firewall", AvitoCaptchaDetector.Classify(html));
    }

    [Fact]
    public void IsCaptchaHtml_StaticIpBlockPageWithoutFirewallContainer_DetectedAsFirewall()
    {
        // Реальный снимок 2026: Avito отдаёт отдельную HTML-страницу без .firewall-container
        // и без виджета капчи. Заголовок в <title>/<h1>, советы про VPN/«в самолёте»,
        // ссылка в поддержку и авто-reload с hash #block.
        const string html = """
            <html><head>
              <title>Доступ ограничен: проблема с IP</title>
            </head>
            <body>
              <div class="container">
                <div class="content">
                  <h1>Доступ ограничен: проблема с IP</h1>
                  <p>Иногда такое случается — подождите немного и обновите страницу. Если проблема не уходит, вот что можно сделать:</p>
                  <ul>
                    <li>Отключить VPN.</li>
                    <li>Включить и выключить режим «В самолёте».</li>
                    <li>Подключиться к другой сети.</li>
                    <li>Перезагрузить роутер.</li>
                  </ul>
                  <p>
                    Если и это не сработает, напишите <a href="https://support.avito.ru/request/720">в поддержку</a>.
                    В письме укажите город, провайдера и IP-адрес (его можно посмотреть на yandex.ru/internet).
                  </p>
                </div>
              </div>
              <script>
                if (window.location.hash != "#block") {
                    setTimeout(function(){
                        window.location.replace(window.location.pathname + window.location.search + "#block");
                        window.location.reload();
                    }, "1000");
                }
              </script>
            </body></html>
            """;

        Assert.True(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Equal("firewall", AvitoCaptchaDetector.Classify(html));
        Assert.False(AvitoCaptchaDetector.HasGeeTestWidget(html));
    }

    [Fact]
    public void IsCaptchaHtml_StaticIpBlockMarkersWithoutTitle_DetectedAsFirewall()
    {
        // Страховка: даже без фразы «Доступ ограничен» уникальные маркеры страницы
        // (hash #block, тикет поддержки, совет про VPN) должны дать firewall.
        const string html = """
            <html><body>
              <ul><li>Отключить VPN.</li><li>режим «В самолёте»</li></ul>
              <a href="https://support.avito.ru/request/720">поддержка</a>
              <script>
                if (window.location.hash != "#block") { window.location.reload(); }
              </script>
            </body></html>
            """;

        Assert.True(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.Equal("firewall", AvitoCaptchaDetector.Classify(html));
    }
}
