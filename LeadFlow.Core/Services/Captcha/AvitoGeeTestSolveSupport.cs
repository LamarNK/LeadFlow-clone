using System.Text.Json;
using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.Captcha;

public static class AvitoGeeTestSolveSupport
{
    /// <summary>
    /// Одновременно в RuCaptcha могут идти шестнадцать независимых задач GeeTest. Ограничение
    /// оставляет предохранитель от бесконечной лавины, но обслуживает крупную ферму браузеров пачками.
    /// </summary>
    public const int MaxConcurrentGeeTestSolves = 16;

    public const string AvitoCaptchaId = "2d9c743cf7d63dbc9db578a608196bcd";
    public const string VerifyPath = "/web/3/firewallCaptcha/verify";

    /// <summary>
    /// Запускает обработчик firewall и возвращает именно выбранный Avito вид проверки.
    /// Наличие скрытых контейнеров hCaptcha/GeeTest не считается выбором типа.
    /// </summary>
    public static string BuildActivateAndProbeScript() =>
        """
        (async () => {
          const isVisible = (el) => {
            if (!el) return false;
            for (let node = el; node && node.nodeType === Node.ELEMENT_NODE; node = node.parentElement) {
              const style = window.getComputedStyle(node);
              if (style.display === 'none' || style.visibility === 'hidden') return false;
            }
            const rect = el.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
          };
          const detectKind = () => {
            if (isVisible(document.querySelector('#geetest_captcha, .geetest_widget, [data-geetest]'))) return 'geetest';
            if (isVisible(document.querySelector('#h-captcha, .h-captcha, [data-hcaptcha-widget-id]'))) return 'hcaptcha';
            if (isVisible(document.querySelector('#inner-captcha, .js-form-captcha, .form-captcha'))) return 'internal';
            return 'unknown';
          };
          const getHcaptchaSiteKey = () => {
            const element = document.querySelector('#h-captcha [data-sitekey], .h-captcha[data-sitekey], [data-hcaptcha-widget-id][data-sitekey]');
            if (element && element.getAttribute('data-sitekey')) return element.getAttribute('data-sitekey');
            const frame = [...document.querySelectorAll('iframe[src*="hcaptcha.com"]')].find(isVisible)
              || document.querySelector('iframe[src*="hcaptcha.com"]');
            if (!frame || !frame.src) return null;
            try { return new URL(frame.src, location.href).searchParams.get('sitekey'); } catch { return null; }
          };
          const getInternalImage = async (candidate) => {
            const image = document.querySelector('#inner-captcha .js-form-captcha-image, #inner-captcha img');
            const source = candidate || (image && (image.currentSrc || image.getAttribute('src')));
            if (!source || typeof source !== 'string') return null;
            if (source.startsWith('data:image/')) return source;
            if (!/^(?:https?:)?\//i.test(source)) return source;
            try {
              const response = await fetch(source, { credentials: 'include' });
              if (!response.ok) return null;
              const blob = await response.blob();
              return await new Promise((resolve) => {
                const reader = new FileReader();
                reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : null);
                reader.onerror = () => resolve(null);
                reader.readAsDataURL(blob);
              });
            } catch { return null; }
          };
          const fromFirewallResponse = (captcha) => {
            if (!captcha || typeof captcha !== 'object') return { kind: 'unknown', siteKey: null };
            const known = captcha.geeTest ? ['geetest', captcha.geeTest]
              : captcha.hCaptcha ? ['hcaptcha', captcha.hCaptcha]
              : captcha.internalCaptcha ? ['internal', captcha.internalCaptcha]
              : [String(captcha.type || captcha.kind || '').toLowerCase(), captcha];
            const rawKind = String(known[0] || '').toLowerCase();
            const kind = rawKind.includes('geetest') ? 'geetest'
              : rawKind.includes('hcaptcha') ? 'hcaptcha'
              : rawKind.includes('internal') ? 'internal'
              : 'unknown';
            const details = known[1] && typeof known[1] === 'object' ? known[1] : {};
            const siteKey = details.sitekey || details.siteKey || details.websiteKey
              || captcha.sitekey || captcha.siteKey || captcha.websiteKey || null;
            const image = details.image || captcha.image || null;
            return {
              kind,
              siteKey: typeof siteKey === 'string' ? siteKey : null,
              image: typeof image === 'string' ? image : null
            };
          };
          const roots = [
            document.querySelector('[role="dialog"][aria-modal="true"]'),
            document.querySelector('[aria-modal="true"]'),
            document.querySelector('[data-scroll-lock-ignore]'),
            document.querySelector('.js-firewall-form'),
            document.querySelector('.firewall-container')
          ].filter(Boolean);
          const isContinue = (el) => /Продолжить/i.test((el.innerText || el.textContent || '').replace(/\s+/g, ' '));
          let kind = detectKind();
          let clicked = false;
          let serverKind = 'unknown';
          let siteKey = getHcaptchaSiteKey();
          let imageData = kind === 'internal' ? await getInternalImage() : null;
          if (kind === 'unknown') {
            for (const root of roots) {
              const button = [...root.querySelectorAll('button, [role="button"], input[type="submit"]')].find(isContinue);
              if (button) {
                button.click();
                clicked = true;
                break;
              }
            }
          }
          const deadline = Date.now() + 8000;
          while (kind === 'unknown' && Date.now() < deadline) {
            await new Promise(resolve => setTimeout(resolve, 250));
            kind = detectKind();
            if (kind === 'hcaptcha' && !siteKey) siteKey = getHcaptchaSiteKey();
            if (kind === 'internal' && !imageData) imageData = await getInternalImage();
          }
          // На SPA-версии Avito виджет иногда не монтируется в DOM, хотя сервер уже
          // выбрал тип проверки. Это тот же запрос, который выполняет штатный скрипт страницы.
          if (kind === 'unknown') {
            try {
              const res = await fetch('/web/5/firewallCaptcha/get', {
                method: 'POST',
                credentials: 'include',
                headers: { 'content-type': 'application/json', 'accept': 'application/json' },
                body: JSON.stringify({ refreshInternalCaptcha: false })
              });
              const response = await res.json();
              const resolved = fromFirewallResponse(response && response.success && response.success.result
                ? response.success.result.captcha : null);
              serverKind = resolved.kind;
              if (resolved.kind !== 'unknown') kind = resolved.kind;
              if (!siteKey && resolved.siteKey) siteKey = resolved.siteKey;
              if (!imageData && resolved.image) imageData = await getInternalImage(resolved.image);
            } catch {}
          }
          if (kind === 'internal' && !imageData) imageData = await getInternalImage();
          return JSON.stringify({ kind, serverKind, siteKey, image: imageData, clicked, userAgent: navigator.userAgent || '' });
        })()
        """;

    /// <summary>
    /// SPA-модалка: GeeTest bind стартует только по клику «Продолжить».
    /// На классической странице с <c>#geetest_captcha</c> кликать не нужно.
    /// </summary>
    public static string BuildClickContinueScript() =>
        """
        (() => {
            const roots = [
                document.querySelector('[role="dialog"][aria-modal="true"]'),
                document.querySelector('[aria-modal="true"]'),
                document.querySelector('[data-scroll-lock-ignore]'),
                document.querySelector('.js-firewall-form'),
                document.querySelector('.firewall-container'),
                document
            ].filter(Boolean);
            const isContinue = (el) => /Продолжить/i.test((el.innerText || el.textContent || '').replace(/\s+/g, ' '));
            for (const root of roots) {
                const btn = [...root.querySelectorAll('button, [role="button"], input[type="submit"]')].find(isContinue);
                if (btn) {
                    btn.click();
                    return true;
                }
            }
            return false;
        })()
        """;

    /// <summary>
    /// Быстрый pre-check. Firewall без видимого GeeTest не годится: сервер Avito ещё может
    /// выбрать hCaptcha или внутреннюю картинку. Окончательное решение принимает probe в браузере.
    /// </summary>
    public static bool CanAutoSolve(string? html, string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && AvitoCaptchaDetector.HasGeeTestWidget(html);

    /// <summary>
    /// Новую платную задачу RuCaptcha создаём только пока Avito ещё требует проверку.
    /// Экран «Проверка пройдена, перенаправление…» — это не новая капча.
    /// </summary>
    public static bool ShouldCreateProviderTask(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || AvitoCaptchaRedirectRecovery.RequiresRecovery(html))
        {
            return false;
        }

        return AvitoCaptchaDetector.IsCaptchaHtml(html)
               || AvitoCaptchaDetector.CanAttemptGeeTestSolve(html);
    }

    /// <summary>Извлекает ключ hCaptcha из статической firewall-разметки Avito.</summary>
    public static string? ExtractHCaptchaSiteKey(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"(?:h-captcha|hcaptcha)[^>]*data-sitekey\s*=\s*[""']([^""']+)[""']|data-sitekey\s*=\s*[""']([^""']+)[""'][^>]*(?:h-captcha|hcaptcha)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static AvitoCaptchaActivation ParseActivation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AvitoCaptchaActivation.Unknown;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            var serverKind = root.TryGetProperty("serverKind", out var serverKindProp)
                ? serverKindProp.GetString()
                : null;
            var parsedKind = AvitoCaptchaKindExtensions.Parse(kind);
            if (parsedKind == AvitoCaptchaKind.Unknown)
            {
                parsedKind = AvitoCaptchaKindExtensions.Parse(serverKind);
            }
            var clicked = root.TryGetProperty("clicked", out var clickedProp)
                && clickedProp.ValueKind == JsonValueKind.True;
            var userAgent = root.TryGetProperty("userAgent", out var userAgentProp)
                ? userAgentProp.GetString()
                : null;
            var siteKey = root.TryGetProperty("siteKey", out var siteKeyProp)
                ? siteKeyProp.GetString()?.Trim()
                : null;
            var imageData = root.TryGetProperty("image", out var imageProp)
                ? imageProp.GetString()
                : null;

            return new AvitoCaptchaActivation(parsedKind, clicked, userAgent, siteKey, serverKind, imageData);
        }
        catch (JsonException)
        {
            return AvitoCaptchaActivation.Unknown;
        }
    }

    public static AvitoVerifyResult ParseVerifyResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AvitoVerifyResult.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusProp) && statusProp.TryGetInt32(out var value)
                ? (int?)value
                : null;
            var verified = root.TryGetProperty("verified", out var verifiedProp)
                && verifiedProp.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
            var error = root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : null;
            return new AvitoVerifyResult(status, verified, text, error);
        }
        catch (JsonException)
        {
            return new AvitoVerifyResult(null, false, null, "invalid-result");
        }
    }

    /// <summary>
    /// На классической странице <c>#geetest_captcha</c> — только заглушка: слайдер
    /// открывается по клику «Продолжить» (<c>initGeetest4 product: bind</c>).
    /// </summary>
    public static bool NeedsContinueClick(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("Продолжить", StringComparison.Ordinal))
        {
            return false;
        }

        if (!AvitoCaptchaDetector.CanAttemptGeeTestSolve(html))
        {
            return false;
        }

        if (html.Contains("geetest_holder", StringComparison.OrdinalIgnoreCase)
            || html.Contains("geetest_box", StringComparison.OrdinalIgnoreCase)
            || html.Contains("geetest_popup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !System.Text.RegularExpressions.Regex.IsMatch(
            html,
            @"name=[""']captcha-response[""'][^>]*value=[""'][^""']{20,}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

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
                const hidden = document.querySelector('input[name="captcha-response"]');
                if (hidden) {
                  hidden.value = JSON.stringify(payload);
                }
                const cubeEl = document.getElementById('cubeResult');
                const cube = cubeEl ? String(cubeEl.innerHTML || '') : '';
                const inputEl = document.querySelector('#form-input');
                const hEl = document.querySelector('#h-captcha-response');
                const body = Object.assign({
                  captcha: inputEl && inputEl.value ? String(inputEl.value).trim() : '',
                  hCaptchaResponse: hEl && hEl.value ? String(hEl.value) : ''
                }, payload);
                const res = await fetch('{{VerifyPath}}', {
                  method: 'POST',
                  credentials: 'include',
                  headers: {
                    'content-type': 'application/json',
                    'accept': 'application/json',
                    'X-Cube': cube
                  },
                  body: JSON.stringify(body)
                });
                const text = await res.text();
                let verified = false;
                try {
                  const json = JSON.parse(text);
                  const result = json && json.success && json.success.result;
                  verified = !!(result && result.verified);
                } catch {}
                if (verified) {
                  document.cookie = 'captcha_solved=1; Path=/; Max-Age=10; SameSite=Lax';
                }
                return JSON.stringify({ ok: res.ok, status: res.status, verified, text: String(text || '').slice(0, 2000) });
              } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message ? e.message : e) });
              }
            })()
            """;
    }

    /// <summary>
    /// Подтверждает hCaptcha через штатный firewall endpoint Avito. В отличие от GeeTest
    /// этому endpoint нужен только <c>hCaptchaResponse</c>, без полей GeeTest v4.
    /// </summary>
    public static string BuildHCaptchaVerifyScript(HCaptchaSolution solution)
    {
        var token = JsonSerializer.Serialize(solution.Token);

        return $$"""
            (async () => {
              try {
                const hCaptchaResponse = {{token}};
                document.querySelectorAll('#h-captcha-response, textarea[name="h-captcha-response"], input[name="h-captcha-response"]').forEach((el) => {
                  el.value = hCaptchaResponse;
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                });
                const cubeEl = document.getElementById('cubeResult');
                const cube = cubeEl ? String(cubeEl.innerHTML || '') : '';
                const inputEl = document.querySelector('#form-input');
                const res = await fetch('{{VerifyPath}}', {
                  method: 'POST',
                  credentials: 'include',
                  headers: {
                    'content-type': 'application/json',
                    'accept': 'application/json',
                    'X-Cube': cube
                  },
                  body: JSON.stringify({
                    captcha: inputEl && inputEl.value ? String(inputEl.value).trim() : '',
                    hCaptchaResponse
                  })
                });
                const text = await res.text();
                let verified = false;
                try {
                  const json = JSON.parse(text);
                  const result = json && json.success && json.success.result;
                  verified = !!(result && result.verified);
                } catch {}
                if (verified) {
                  document.cookie = 'captcha_solved=1; Path=/; Max-Age=10; SameSite=Lax';
                }
                return JSON.stringify({ ok: res.ok, status: res.status, verified, text: String(text || '').slice(0, 2000) });
              } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message ? e.message : e) });
              }
            })()
            """;
    }

    /// <summary>Отправляет распознанный текст встроенной image-captcha в firewall Avito.</summary>
    public static string BuildInternalCaptchaVerifyScript(ImageCaptchaSolution solution)
    {
        var captcha = JsonSerializer.Serialize(solution.Text);

        return $$"""
            (async () => {
              try {
                const captcha = {{captcha}};
                const input = document.querySelector('#form-input');
                if (input) {
                  input.value = captcha;
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                }
                const cubeEl = document.getElementById('cubeResult');
                const cube = cubeEl ? String(cubeEl.innerHTML || '') : '';
                const res = await fetch('{{VerifyPath}}', {
                  method: 'POST',
                  credentials: 'include',
                  headers: {
                    'content-type': 'application/json',
                    'accept': 'application/json',
                    'X-Cube': cube
                  },
                  body: JSON.stringify({ captcha, hCaptchaResponse: '' })
                });
                const text = await res.text();
                let verified = false;
                try {
                  const json = JSON.parse(text);
                  const result = json && json.success && json.success.result;
                  verified = !!(result && result.verified);
                } catch {}
                if (verified) {
                  document.cookie = 'captcha_solved=1; Path=/; Max-Age=10; SameSite=Lax';
                }
                return JSON.stringify({ ok: res.ok, status: res.status, verified, text: String(text || '').slice(0, 2000) });
              } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message ? e.message : e) });
              }
            })()
            """;
    }
}
