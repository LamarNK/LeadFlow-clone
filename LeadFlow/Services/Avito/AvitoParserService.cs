using LeadFlow.Models;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LeadFlow.Services.Avito;

public class AvitoParserService
{
    public ProfileResult ParseProfilePage(string html, Guid? accountId = null)
    {
        var result = new ProfileResult();
        if (string.IsNullOrEmpty(html)) return result;

        // 1️⃣ Счётчики из вкладок (только цифры)
        result.ActiveCount = ExtractCounter(html, "tab(active)");
        result.BlockedCount = ExtractCounter(html, "tab(rejected)");
        result.DraftsCount = ExtractCounter(html, "tab(drafts)");

        // 2️⃣ Парсинг активных объявлений
        var snippetMatches = Regex.Matches(html, @"data-marker=""item-snippet/(\d+)""");
        
        foreach (Match match in snippetMatches)
        {
            if (!match.Success) continue;
            var id = match.Groups[1].Value;
            
            int startIndex = match.Index;
            int endIndex = html.IndexOf("</div>", startIndex + 2000);
            if (endIndex == -1) endIndex = startIndex + 3000;
            
            string snippetHtml = html.Substring(startIndex, endIndex - startIndex);

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty };
            ad.Title = ExtractSingle(snippetHtml, @"class=""styles-title-UJzSB"">([^<]+)");
            ad.City = ExtractSingle(snippetHtml, @"class=""styles-address-I7r1Q"">([^<]+)");
            
            // Пропускаем заблокированные
            if (snippetHtml.Contains("styles-status-name_red-")) continue;

            ad.Views = ParseInt(ExtractSingle(snippetHtml, @"role-marker=""views"">.*?<span[^>]*>(\d+)"));
            ad.Contacts = ParseInt(ExtractSingle(snippetHtml, @"role-marker=""contacts"">.*?<span[^>]*>(\d+)"));
            
            result.ActiveAds.Add(ad);
        }

        return result;
    }

    private int ExtractCounter(string html, string tabMarker)
    {
        var pattern = $@"data-marker=""profile-items-tab/{tabMarker}"".*?class=""[^""]*styles-module-counter[^""]*"".*?>(\d+)<";
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private string ExtractSingle(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;
}

public class ProfileResult
{
    public int ActiveCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();
}
