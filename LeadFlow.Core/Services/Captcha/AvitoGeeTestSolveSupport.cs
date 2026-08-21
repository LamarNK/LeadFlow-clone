using System.Text.Json;
using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.Captcha;

public static class AvitoGeeTestSolveSupport
{
    public const string AvitoCaptchaId = "2d9c743cf7d63dbc9db578a608196bcd";
    public const string VerifyPath = "/web/1/firewallCaptcha/verify";

    public static bool CanAutoSolve(string? html, string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && AvitoCaptchaDetector.HasGeeTestWidget(html);

    public static string BuildVerifyScript(GeeTestV4Solution solution)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["captcha_id"] = solution.CaptchaId,
            ["lot_number"] = solution.LotNumber,
            ["pass_token"] = solution.PassToken,
            ["gen_time"] = solution.GenTime,
            ["captcha_output"] = solution.CaptchaOutput
        });

        return $$"""
            (async () => {
              try {
                const payload = {{payload}};
                const res = await fetch('{{VerifyPath}}', {
                  method: 'POST',
                  credentials: 'include',
                  headers: { 'content-type': 'application/json', 'accept': 'application/json' },
                  body: JSON.stringify(payload)
                });
                const text = await res.text();
                return JSON.stringify({ ok: res.ok, status: res.status, text: String(text || '').slice(0, 2000) });
              } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message ? e.message : e) });
              }
            })()
            """;
    }
}
