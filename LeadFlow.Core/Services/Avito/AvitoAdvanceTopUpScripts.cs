using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Стабильные маршруты/селекторы и JS-скрипты для ручного пополнения аванса Avito.
/// Только стабильные data-marker'ы и маршруты — без CSS-module классов.
/// Чистые функции — покрываются юнит-тестами без браузера.
/// </summary>
public static class AvitoAdvanceTopUpScripts
{
    /// <summary>Страница пополнения аванса Avito.</summary>
    public const string AdvancePageUrl = "https://www.avito.ru/account/advance";

    /// <summary>Поле суммы пополнения.</summary>
    public const string AmountInputSelector = "input[data-marker='amount/input']";

    /// <summary>Кнопка подтверждения суммы (переход к выбору способа оплаты).</summary>
    public const string SubmitButtonSelector = "button[data-marker='submit-btn']";

    /// <summary>Кнопка оплаты после выбора способа.</summary>
    public const string PayButtonSelector = "button[data-marker='payButton']";

    /// <summary>
    /// Страница выбора способа оплаты: СБП-маркер, карусель вариантов, кнопка оплаты
    /// или заголовок заказа. Используется как ожидание после «Подтвердить» сумму.
    /// </summary>
    public const string PaymentPageReadySelector =
        "[data-marker='payButton'], [data-marker='sbp'], [data-marker='orderId'], [data-marker='paymentVariant']";

    /// <summary>
    /// Стабильные маркеры варианта оплаты «СБП» (Система быстрых платежей).
    /// Проверяются по порядку; выбор считается валидным только если найден и кликнут
    /// один из этих маркеров, а не произвольный элемент списка способов.
    /// </summary>
    public static readonly string[] SbpVariantSelectors =
    [
        // Текущая вёрстка Avito: кликабельный paymentVariant содержит вложенный span[data-marker='sbp'].
        // Оставляем этот точный селектор первым, чтобы не зависеть от порядка остальных вариантов.
        "[data-marker='paymentVariant'] [data-marker='sbp']",
        "[data-marker='payment-method/sbp']",
        "[data-marker='payment-method/sbp/option']",
        "[data-marker='payment-method/sbp/radio']",
        "[data-marker='payment-method/sbp/card']",
        "[data-marker='sbp']",
        "[data-marker='sbp/option']",
        "[data-marker='sbp/radio']"
    ];

    /// <summary>
    /// QR-изображение. На актуальной странице СБП Avito маркер и alt могут отсутствовать,
    /// а QR может быть отрисован как <c>img</c>, <c>canvas</c> или <c>svg</c>.
    /// </summary>
    public const string QrImageSelector =
        "[data-marker='sbp/qr'] img, [data-marker='payment/qr'] img, [data-marker='qr-code'] img, img[alt='qr'], img[alt='QR']";

    /// <summary>Корневой контейнер подтверждения СБП (для проверки, что QR действительно на экране СБП).</summary>
    public const string SbpConfirmationSelector =
        "[data-marker='sbp/confirmation'], [data-marker='payment/sbp/confirmation'], [data-marker='sbp/qr']";

    /// <summary>
    /// Ожидание экрана QR: маркеры, <c>img[alt=qr]</c> или квадратный визуальный элемент
    /// рядом с заголовком «Подтвердите платёж по СБП».
    /// </summary>
    public const string QrReadyWaitExpression = """
        () => {
            const isQrSized = (el) => {
                const rect = el.getBoundingClientRect();
                const minSide = Math.min(rect.width, rect.height);
                return minSide >= 80
                    && Math.max(rect.width, rect.height) <= 500
                    && Math.abs(rect.width - rect.height) <= Math.max(16, minSide * 0.2);
            };
            const findQrVisual = (root) => {
                if (!root || !root.querySelectorAll) return null;
                for (const el of root.querySelectorAll('img,canvas,svg')) {
                    if (isQrSized(el)) return el;
                }
                return null;
            };
            const byMarker = document.querySelector("[data-marker='sbp/confirmation'], [data-marker='payment/sbp/confirmation'], [data-marker='sbp/qr']");
            const byAlt = document.querySelector("img[alt='qr'], img[alt='QR']");
            if (byMarker || byAlt) return true;
            const nodes = document.querySelectorAll('h1,h2,h3,h4,h5,p,div,span');
            for (const el of nodes) {
                const text = (el.textContent || '').replace(/\s+/g, ' ');
                if (text.length > 300) continue;
                if (!text.includes('Подтвердите платёж по СБП') && !text.includes('отсканируйте QR')) continue;
                let container = el;
                for (let depth = 0; container && depth < 7; depth++, container = container.parentElement) {
                    if (findQrVisual(container)) return true;
                }
            }
            return false;
        }
        """;

    /// <summary>Форматирует сумму для ввода (инвариантная культура, без лишних нулей).</summary>
    public static string FormatAmount(decimal amount) =>
        amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Вводит сумму в поле <c>input[data-marker="amount/input"]</c> через React value tracker
    /// (нативный setter + input/change события), чтобы SPA увидел значение.
    /// </summary>
    public static string BuildEnterAmountScript(decimal amount)
    {
        var value = JsonSerializer.Serialize(FormatAmount(amount));
        return $$"""
            (() => {
                const input = document.querySelector("input[data-marker='amount/input']");
                if (!input) return false;
                const value = {{value}};
                const proto = input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                if (setter) {
                    setter.call(input, value);
                } else {
                    input.value = value;
                }
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
            })()
            """;
    }

    /// <summary>
    /// Выбирает вариант оплаты «СБП» по стабильному маркеру и кликает его.
    /// Возвращает <c>true</c> только если найден именно СБП-маркер (уже выбранный
    /// пункт карусели тоже считается успехом — клик не нужен).
    /// </summary>
    public static string BuildSelectSbpScript()
    {
        var selectors = JsonSerializer.Serialize(SbpVariantSelectors);
        return $$"""
            (() => {
                const selectors = {{selectors}};
                for (const selector of selectors) {
                    const el = document.querySelector(selector);
                    if (!el) continue;
                    const clickable = el.closest('[role="option"], [data-marker="paymentVariant"], button, a, [role="button"], label, li') || el;
                    if (clickable.getAttribute('aria-selected') === 'true') return true;
                    try {
                        clickable.scrollIntoView({ block: 'center', inline: 'nearest' });
                    } catch {}
                    try {
                        clickable.click();
                        return true;
                    } catch {
                        try { el.click(); return true; } catch { return false; }
                    }
                }
                return false;
            })()
            """;
    }

    /// <summary>
    /// Снимает QR-изображение с экрана подтверждения СБП. Ищет маркер, <c>img[alt=qr]</c>
    /// или квадратный img/canvas/svg рядом с заголовком «Подтвердите платёж по СБП».
    /// Предпочитает PNG-байты, иначе <c>src</c>. Никогда не логирует содержимое.
    /// </summary>
    public static string BuildCaptureQrScript()
    {
        var qrSelector = JsonSerializer.Serialize(QrImageSelector);
        var confirmationSelector = JsonSerializer.Serialize(SbpConfirmationSelector);
        return $$"""
            (async () => {
                const isQrSized = (el) => {
                    const rect = el.getBoundingClientRect();
                    const minSide = Math.min(rect.width, rect.height);
                    return minSide >= 80
                        && Math.max(rect.width, rect.height) <= 500
                        && Math.abs(rect.width - rect.height) <= Math.max(16, minSide * 0.2);
                };
                const findQrVisual = (root) => {
                    if (!root || !root.querySelectorAll) return null;
                    for (const el of root.querySelectorAll('img,canvas,svg')) {
                        if (isQrSized(el)) return el;
                    }
                    return null;
                };
                const byMarker = document.querySelector({{confirmationSelector}});
                const byAlt = document.querySelector("img[alt='qr'], img[alt='QR']");
                let confirmation = byMarker || byAlt;
                let visual = byAlt || (byMarker && findQrVisual(byMarker));
                if (!confirmation) {
                    const nodes = document.querySelectorAll('h1,h2,h3,h4,h5,p,div,span');
                    for (const el of nodes) {
                        const text = (el.textContent || '').replace(/\s+/g, ' ');
                        if (text.length > 300) continue;
                        if (text.includes('Подтвердите платёж по СБП') || text.includes('отсканируйте QR')) {
                            confirmation = el;
                            let container = el;
                            for (let depth = 0; container && depth < 7; depth++, container = container.parentElement) {
                                visual = findQrVisual(container);
                                if (visual) {
                                    confirmation = container;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                }
                if (!confirmation) return JSON.stringify({ found: false, reason: 'no_sbp_confirmation' });
                visual = visual
                    || (confirmation.matches && confirmation.matches('img,canvas,svg') ? confirmation : null)
                    || findQrVisual(confirmation)
                    || document.querySelector({{qrSelector}});
                if (!visual) return JSON.stringify({ found: false, reason: 'no_qr_visual' });

                const visualTag = (visual.tagName || '').toUpperCase();
                if (visualTag === 'CANVAS') {
                    try {
                        const dataUrl = visual.toDataURL('image/png');
                        if (dataUrl) return JSON.stringify({ found: true, dataUrl, src: null });
                    } catch {}
                    return JSON.stringify({ found: false, reason: 'canvas_export_failed' });
                }

                if (visualTag === 'SVG') {
                    try {
                        const rect = visual.getBoundingClientRect();
                        const width = Math.max(1, Math.round(rect.width || 256));
                        const height = Math.max(1, Math.round(rect.height || 256));
                        const xml = new XMLSerializer().serializeToString(visual);
                        const blobUrl = URL.createObjectURL(new Blob([xml], { type: 'image/svg+xml;charset=utf-8' }));
                        try {
                            const raster = await new Promise((resolve) => {
                                const image = new Image();
                                image.onload = () => {
                                    try {
                                        const canvas = document.createElement('canvas');
                                        canvas.width = width;
                                        canvas.height = height;
                                        canvas.getContext('2d').drawImage(image, 0, 0, width, height);
                                        resolve(canvas.toDataURL('image/png'));
                                    } catch {
                                        resolve(null);
                                    }
                                };
                                image.onerror = () => resolve(null);
                                image.src = blobUrl;
                            });
                            if (raster) return JSON.stringify({ found: true, dataUrl: raster, src: null });
                        } finally {
                            URL.revokeObjectURL(blobUrl);
                        }
                    } catch {}
                    return JSON.stringify({ found: false, reason: 'svg_export_failed' });
                }

                const img = visual;
                if (!img.complete || (img.naturalWidth === 0 && !img.src)) {
                    await new Promise((resolve) => {
                        const done = () => resolve();
                        img.addEventListener('load', done, { once: true });
                        img.addEventListener('error', done, { once: true });
                        setTimeout(done, 2000);
                    });
                }
                let dataUrl = null;
                let src = img.getAttribute('src') || img.currentSrc || null;
                try {
                    const canvas = document.createElement('canvas');
                    const naturalW = img.naturalWidth || img.width;
                    const naturalH = img.naturalHeight || img.height;
                    if (naturalW > 0 && naturalH > 0) {
                        canvas.width = naturalW;
                        canvas.height = naturalH;
                        const ctx = canvas.getContext('2d');
                        ctx.drawImage(img, 0, 0, naturalW, naturalH);
                        dataUrl = canvas.toDataURL('image/png');
                    }
                } catch {}
                if (!dataUrl && src && src.indexOf('blob:') === 0) {
                    try {
                        const resp = await fetch(src);
                        const blob = await resp.blob();
                        dataUrl = await new Promise((resolve) => {
                            const reader = new FileReader();
                            reader.onload = () => resolve(reader.result);
                            reader.onerror = () => resolve(null);
                            reader.readAsDataURL(blob);
                        });
                    } catch {}
                }
                if (!dataUrl && src && src.indexOf('data:') === 0) {
                    dataUrl = src;
                }
                if (!dataUrl && !src) return JSON.stringify({ found: false, reason: 'no_qr_payload' });
                return JSON.stringify({ found: true, dataUrl, src });
            })()
            """;
    }

    /// <summary>Снимок состояния страницы пополнения аванса (для диагностики и ожиданий).</summary>
    public static string BuildProbeAdvancePageScript()
    {
        var sbpSelectors = JsonSerializer.Serialize(SbpVariantSelectors);
        return $$"""
            (() => {
                const amountInput = !!document.querySelector("input[data-marker='amount/input']");
                const submitBtn = !!document.querySelector("button[data-marker='submit-btn']");
                const payBtn = !!document.querySelector("button[data-marker='payButton']");
                const sbpSelectors = {{sbpSelectors}};
                const sbpVariant = sbpSelectors.some((s) => !!document.querySelector(s));
                const sbpConfirmation = !!document.querySelector("[data-marker='sbp/confirmation'], [data-marker='payment/sbp/confirmation'], [data-marker='sbp/qr']");
                const qrImage = !!document.querySelector("[data-marker='sbp/qr'] img, [data-marker='payment/qr'] img, [data-marker='qr-code'] img, img[alt='qr'], img[alt='QR']");
                return JSON.stringify({
                    url: window.location.href,
                    amountInput,
                    submitBtn,
                    payBtn,
                    sbpVariant,
                    sbpConfirmation,
                    qrImage
                });
            })()
            """;
    }

    /// <summary>Результат снятия QR-изображения.</summary>
    public sealed record QrCaptureResult(bool Found, string? DataUrl, string? Src, string? Reason);

    public static QrCaptureResult? TryParseQrCapture(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var found = root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
            var dataUrl = root.TryGetProperty("dataUrl", out var d) ? d.GetString() : null;
            var src = root.TryGetProperty("src", out var s) ? s.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return new QrCaptureResult(found, dataUrl, src, reason);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Извлекает base64-часть из data URL (<c>data:image/png;base64,....</c>).
    /// Возвращает <c>null</c>, если data URL некорректен.
    /// </summary>
    public static string? ExtractBase64FromDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var header = dataUrl[..comma];
        if (!header.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || !header.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = dataUrl[(comma + 1)..].Trim();
        return payload.Length == 0 ? null : payload;
    }

    /// <summary>
    /// Санитизирует диагностическое сообщение: убирает переносы, обрезает длину,
    /// не пропускает потенциально чувствительные токены. QR-данные сюда не попадают.
    /// </summary>
    public static string SanitizeDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Неизвестная ошибка пополнения аванса.";
        }

        var cleaned = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        const int maxLength = 500;
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength] + "…";
        }

        return cleaned;
    }

    private static string UnwrapJsonString(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(t) ?? t;
            }
            catch
            {
                return t;
            }
        }

        return t;
    }
}
