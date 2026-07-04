using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class AdsPowerErrorMessageNormalizerTests
{
    [Fact]
    public void NormalizeForDisplay_FormatsProfileInUseMessage()
    {
        const string raw =
            "AdsPower: [k1cu2550] is being used by [a.pakin797@gmail.com] and is not allowed to open (code -1)";

        var normalized = AdsPowerErrorMessageNormalizer.NormalizeForDisplay(raw);

        Assert.Contains("k1cu2550", normalized);
        Assert.Contains("a.pakin797@gmail.com", normalized);
        Assert.Contains("уже открыт пользователем", normalized);
        Assert.DoesNotContain("is being used by", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikeProfileInUse_DetectsEnglishApiMessage()
    {
        const string raw =
            "[k1cu2550] is being used by [a.pakin797@gmail.com] and is not allowed to open";

        Assert.True(AdsPowerErrorMessageNormalizer.LooksLikeProfileInUse(raw));
    }
}