using System.Text.RegularExpressions;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Сборка заголовков <c>Sec-CH-UA</c> и <c>Sec-CH-UA-Full-Version-List</c> из строки User-Agent,
/// чтобы они совпадали с подменённым браузером (иначе WebView2/Edge оставляет «родные» бренды Chromium).
/// </summary>
public static class SecChUaHeaders
{
    public sealed record BrowserHintValues(string SecChUa, string SecChUaFullVersionList);

    /// <returns><see langword="null"/> если в UA нет <c>Chrome/</c> (например чистый Firefox в строке).</returns>
    public static BrowserHintValues? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var ua = userAgent.Trim();
        var chromeMatch = Regex.Match(ua, @"Chrome/([\d.]+)", RegexOptions.IgnoreCase);
        if (!chromeMatch.Success)
        {
            return null;
        }

        var chromeToken = chromeMatch.Groups[1].Value;
        var chromeMajor = chromeToken.Split('.')[0];
        var chromeFullList = chromeToken.Contains('.') ? chromeToken : $"{chromeMajor}.0.0.0";

        var edgMatch = Regex.Match(ua, @"Edg/([\d.]+)", RegexOptions.IgnoreCase);
        if (edgMatch.Success)
        {
            var edgToken = edgMatch.Groups[1].Value;
            var edgMajor = edgToken.Split('.')[0];
            var edgFullList = edgToken.Contains('.') ? edgToken : $"{edgMajor}.0.0.0";
            return new BrowserHintValues(
                SecChUa: FormLowEntropy("\"Microsoft Edge\"", edgMajor, chromeMajor),
                SecChUaFullVersionList: FormFullList(
                    ("Microsoft Edge", edgFullList),
                    ("Chromium", chromeFullList)));
        }

        var oprMatch = Regex.Match(ua, @"OPR/([\d.]+)", RegexOptions.IgnoreCase);
        if (oprMatch.Success)
        {
            var oprToken = oprMatch.Groups[1].Value;
            var oprMajor = oprToken.Split('.')[0];
            var oprFullList = oprToken.Contains('.') ? oprToken : $"{oprMajor}.0.0.0";
            return new BrowserHintValues(
                SecChUa: FormLowEntropy("\"Opera\"", oprMajor, chromeMajor),
                SecChUaFullVersionList: FormFullList(
                    ("Opera", oprFullList),
                    ("Chromium", chromeFullList)));
        }

        return new BrowserHintValues(
            SecChUa: FormLowEntropy("\"Google Chrome\"", chromeMajor, chromeMajor),
            SecChUaFullVersionList: FormFullList(
                ("Google Chrome", chromeFullList),
                ("Chromium", chromeFullList)));
    }

    private static string FormLowEntropy(string primaryBrandQuoted, string primaryMajor, string chromiumMajor) =>
        $"{primaryBrandQuoted};v=\"{primaryMajor}\", \"Chromium\";v=\"{chromiumMajor}\", \"Not_A Brand\";v=\"8\"";

    private static string FormFullList(params (string Brand, string FullVersion)[] brands)
    {
        var parts = new List<string>();
        foreach (var (brand, ver) in brands)
        {
            parts.Add($"\"{brand}\";v=\"{ver}\"");
        }

        parts.Add("\"Not_A Brand\";v=\"8.0.0.0\"");
        return string.Join(", ", parts);
    }
}
