using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdvanceTopUpScriptsTests
{
    [Fact]
    public void AdvancePageUrl_IsStableRoute()
    {
        Assert.Equal("https://www.avito.ru/account/advance", AvitoAdvanceTopUpScripts.AdvancePageUrl);
    }

    [Fact]
    public void Selectors_UseStableDataMarkers_NotCssModuleClasses()
    {
        Assert.Equal("input[data-marker='amount/input']", AvitoAdvanceTopUpScripts.AmountInputSelector);
        Assert.Equal("button[data-marker='submit-btn']", AvitoAdvanceTopUpScripts.SubmitButtonSelector);
        Assert.Equal("button[data-marker='payButton']", AvitoAdvanceTopUpScripts.PayButtonSelector);

        // Ни один селектор не должен опираться на CSS-module классы.
        Assert.DoesNotContain("styles-", AvitoAdvanceTopUpScripts.AmountInputSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("styles-", AvitoAdvanceTopUpScripts.SubmitButtonSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("styles-", AvitoAdvanceTopUpScripts.PayButtonSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("styles-", AvitoAdvanceTopUpScripts.QrImageSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("styles-", AvitoAdvanceTopUpScripts.SbpConfirmationSelector, StringComparison.Ordinal);
    }

    [Fact]
    public void SbpVariantSelectors_AllTargetSbpMarkers()
    {
        Assert.NotEmpty(AvitoAdvanceTopUpScripts.SbpVariantSelectors);
        foreach (var selector in AvitoAdvanceTopUpScripts.SbpVariantSelectors)
        {
            Assert.Contains("sbp", selector, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("styles-", selector, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(300, "300")]
    [InlineData(900, "900")]
    [InlineData(2000, "2000")]
    [InlineData(149.5, "149.5")]
    [InlineData(0.5, "0.5")]
    public void FormatAmount_UsesInvariantCulture(decimal amount, string expected)
    {
        Assert.Equal(expected, AvitoAdvanceTopUpScripts.FormatAmount(amount));
    }

    [Fact]
    public void BuildEnterAmountScript_TargetsAmountInput_AndSetsValue()
    {
        var script = AvitoAdvanceTopUpScripts.BuildEnterAmountScript(300m);

        Assert.Contains("input[data-marker='amount/input']", script, StringComparison.Ordinal);
        Assert.Contains("\"300\"", script, StringComparison.Ordinal);
        Assert.Contains("dispatchEvent(new Event('input'", script, StringComparison.Ordinal);
        Assert.Contains("dispatchEvent(new Event('change'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectSbpScript_OnlyClicksSbpMarkers()
    {
        var script = AvitoAdvanceTopUpScripts.BuildSelectSbpScript();

        Assert.Contains("sbp", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click()", script, StringComparison.Ordinal);
        // Не должен кликать другие способы оплаты (карта/кошелёк).
        Assert.DoesNotContain("payment-method/card", script, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-method/wallet", script, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-method/card", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCaptureQrScript_RequiresSbpConfirmation_AndReadsImage()
    {
        var script = AvitoAdvanceTopUpScripts.BuildCaptureQrScript();

        Assert.Contains("sbp/confirmation", script, StringComparison.Ordinal);
        Assert.Contains("toDataURL", script, StringComparison.Ordinal);
        Assert.Contains("getAttribute('src')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractBase64FromDataUrl_ReturnsPayload()
    {
        var dataUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==";
        Assert.Equal("iVBORw0KGgoAAAANSUhEUg==", AvitoAdvanceTopUpScripts.ExtractBase64FromDataUrl(dataUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/qr.png")]
    [InlineData("data:image/png,iVBORw0KGgoAAAANSUhEUg==")] // нет base64
    public void ExtractBase64FromDataUrl_ReturnsNull_ForInvalidInput(string? input)
    {
        Assert.Null(AvitoAdvanceTopUpScripts.ExtractBase64FromDataUrl(input));
    }

    [Fact]
    public void TryParseQrCapture_ParsesFoundResult()
    {
        var raw = "{\"found\":true,\"dataUrl\":\"data:image/png;base64,AAAA\",\"src\":null,\"reason\":null}";
        var result = AvitoAdvanceTopUpScripts.TryParseQrCapture(raw);

        Assert.NotNull(result);
        Assert.True(result.Found);
        Assert.Equal("data:image/png;base64,AAAA", result.DataUrl);
    }

    [Fact]
    public void TryParseQrCapture_ParsesNotFoundResult()
    {
        var raw = "{\"found\":false,\"reason\":\"no_qr_image\"}";
        var result = AvitoAdvanceTopUpScripts.TryParseQrCapture(raw);

        Assert.NotNull(result);
        Assert.False(result.Found);
        Assert.Equal("no_qr_image", result.Reason);
    }

    [Fact]
    public void TryParseQrCapture_ReturnsNull_ForGarbage()
    {
        Assert.Null(AvitoAdvanceTopUpScripts.TryParseQrCapture("not json"));
        Assert.Null(AvitoAdvanceTopUpScripts.TryParseQrCapture(null));
    }

    [Fact]
    public void SanitizeDiagnostic_TrimsAndLimitsLength()
    {
        var longMessage = new string('x', 2000);
        var result = AvitoAdvanceTopUpScripts.SanitizeDiagnostic(longMessage);

        Assert.True(result.Length <= 501);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void SanitizeDiagnostic_RemovesNewlines()
    {
        var result = AvitoAdvanceTopUpScripts.SanitizeDiagnostic("line1\nline2\r\nline3");

        Assert.DoesNotContain("\n", result);
        Assert.DoesNotContain("\r", result);
    }

    [Fact]
    public void SanitizeDiagnostic_ReturnsDefault_ForEmpty()
    {
        Assert.Equal("Неизвестная ошибка пополнения аванса.", AvitoAdvanceTopUpScripts.SanitizeDiagnostic(null));
        Assert.Equal("Неизвестная ошибка пополнения аванса.", AvitoAdvanceTopUpScripts.SanitizeDiagnostic("   "));
    }
}
