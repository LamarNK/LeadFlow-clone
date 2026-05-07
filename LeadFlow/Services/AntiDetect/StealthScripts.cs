using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LeadFlow.Models;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Набор JavaScript-скриптов для скрытия признаков автоматизации в WebView2.
/// Инжектируются через AddScriptToExecuteOnDocumentCreatedAsync.
/// </summary>
public static class StealthScripts
{
    /// <summary>
    /// Основной скрипт для скрытия автоматизации и спойфинга фингерпринта.
    /// </summary>
    public static string GetMainStealthScript(AccountFingerprint? fingerprint = null)
    {
        var fp = fingerprint ?? new AccountFingerprint();
        var fpJson = JsonSerializer.Serialize(fp, new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var script = new StringBuilder();
        script.AppendLine("(function() {");
        script.AppendLine("    'use strict';");
        script.AppendLine();
        script.AppendLine("    // === 1. Скрытие признаков WebDriver ===");
        script.AppendLine("    Object.defineProperty(navigator, 'webdriver', {");
        script.AppendLine("        get: () => false,");
        script.AppendLine("        configurable: true");
        script.AppendLine("    });");
        script.AppendLine();
        script.AppendLine("    // Убираем $cdc_ и $wd_ переменные (детектор Selenium/WebDriver)");
        script.AppendLine("    try {");
        script.AppendLine("        delete document.__webdriver_script_fn;");
        script.AppendLine("        delete document.$cdc_asdjflasutopfhvcZLmcfl_;");
        script.AppendLine("        Object.keys(window).forEach(key => {");
        script.AppendLine("            if (key.startsWith('$cdc_') || key.startsWith('$wd_')) {");
        script.AppendLine("                delete window[key];");
        script.AppendLine("            }");
        script.AppendLine("        });");
        script.AppendLine("    } catch(e) {}");
        script.AppendLine();
        script.AppendLine("    // === 2. Спойфинг навигатора ===");
        script.AppendLine($"    const fp = {fpJson};");
        script.AppendLine();
        script.AppendLine("    // Языки");
        script.AppendLine("    if (fp.languages) {");
        script.AppendLine("        Object.defineProperty(navigator, 'languages', {");
        script.AppendLine("            get: () => fp.languages.split(','),");
        script.AppendLine("            configurable: true");
        script.AppendLine("        });");
        script.AppendLine("        Object.defineProperty(navigator, 'language', {");
        script.AppendLine("            get: () => fp.languages.split(',')[0],");
        script.AppendLine("            configurable: true");
        script.AppendLine("        });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // Платформа");
        script.AppendLine("    if (fp.navigatorPlatform) {");
        script.AppendLine("        Object.defineProperty(navigator, 'platform', {");
        script.AppendLine("            get: () => fp.navigatorPlatform,");
        script.AppendLine("            configurable: true");
        script.AppendLine("        });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // Do Not Track");
        script.AppendLine("    if (fp.doNotTrack === true) {");
        script.AppendLine("        Object.defineProperty(navigator, 'doNotTrack', { get: () => '1', configurable: true });");
        script.AppendLine("    } else if (fp.doNotTrack === false) {");
        script.AppendLine("        Object.defineProperty(navigator, 'doNotTrack', { get: () => null, configurable: true });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 3. Спойфинг экрана и окна ===");
        script.AppendLine("    if (fp.screenResolution) {");
        script.AppendLine("        const [w, h] = fp.screenResolution.split('x').map(Number);");
        script.AppendLine("        Object.defineProperty(screen, 'width', { get: () => w, configurable: true });");
        script.AppendLine("        Object.defineProperty(screen, 'height', { get: () => h, configurable: true });");
        script.AppendLine("        Object.defineProperty(screen, 'availWidth', { get: () => w - 20, configurable: true });");
        script.AppendLine("        Object.defineProperty(screen, 'availHeight', { get: () => h - 100, configurable: true });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    if (fp.viewportWidth && fp.viewportHeight) {");
        script.AppendLine("        Object.defineProperty(window, 'innerWidth', { get: () => fp.viewportWidth, configurable: true });");
        script.AppendLine("        Object.defineProperty(window, 'innerHeight', { get: () => fp.viewportHeight, configurable: true });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 4. Спойфинг часового пояса ===");
        script.AppendLine("    if (fp.timezone) {");
        script.AppendLine("        const originalResolvedOptions = Intl.DateTimeFormat.prototype.resolvedOptions;");
        script.AppendLine("        Intl.DateTimeFormat.prototype.resolvedOptions = function() {");
        script.AppendLine("            const opts = originalResolvedOptions.call(this);");
        script.AppendLine("            return { ...opts, timeZone: fp.timezone };");
        script.AppendLine("        };");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 5. Спойфинг памяти и процессора ===");
        script.AppendLine("    if (fp.deviceMemory) {");
        script.AppendLine("        Object.defineProperty(navigator, 'deviceMemory', {");
        script.AppendLine("            get: () => fp.deviceMemory,");
        script.AppendLine("            configurable: true");
        script.AppendLine("        });");
        script.AppendLine("    }");
        script.AppendLine("    if (fp.hardwareConcurrency) {");
        script.AppendLine("        Object.defineProperty(navigator, 'hardwareConcurrency', {");
        script.AppendLine("            get: () => fp.hardwareConcurrency,");
        script.AppendLine("            configurable: true");
        script.AppendLine("        });");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 5b. WebGL vendor / renderer ===");
        script.AppendLine("    if (fp.spoofWebGl && fp.webGlVendor && fp.webGlRenderer) {");
        script.AppendLine("        const patchGl = (proto) => {");
        script.AppendLine("            const orig = proto.getParameter;");
        script.AppendLine("            proto.getParameter = function(p) {");
        script.AppendLine("                if (p === 37445) return fp.webGlVendor;");
        script.AppendLine("                if (p === 37446) return fp.webGlRenderer;");
        script.AppendLine("                return orig.call(this, p);");
        script.AppendLine("            };");
        script.AppendLine("        };");
        script.AppendLine("        if (typeof WebGLRenderingContext !== 'undefined') patchGl(WebGLRenderingContext.prototype);");
        script.AppendLine("        if (typeof WebGL2RenderingContext !== 'undefined') patchGl(WebGL2RenderingContext.prototype);");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 6. Canvas fingerprint noise (детерминированный, на seed) ===");
        script.AppendLine("    const toSeedInt = (v) => {");
        script.AppendLine("        const src = String(v || 'leadflow-seed').replace(/[^0-9a-f]/gi, '').slice(0, 8) || '9e3779b9';");
        script.AppendLine("        return parseInt(src, 16) >>> 0;");
        script.AppendLine("    };");
        script.AppendLine("    const mix = (a, b) => (((a >>> 0) * 1664525 + (b >>> 0) + 1013904223) >>> 0);");
        script.AppendLine("    const baseSeed = toSeedInt(fp.audioNoiseSeedHex || fp.clientRectsNoiseSeedHex || fp.userAgent);");
        script.AppendLine();
        script.AppendLine("    if (fp.canvasNoise !== false) {");
        script.AppendLine("        const originalToDataURL = HTMLCanvasElement.prototype.toDataURL;");
        script.AppendLine("        const originalToBlob = HTMLCanvasElement.prototype.toBlob;");
        script.AppendLine("        const withStablePixelJitter = (canvas, fn) => {");
        script.AppendLine("            const ctx = canvas.getContext('2d');");
        script.AppendLine("            if (!ctx || !canvas.width || !canvas.height) return fn();");
        script.AppendLine("            const px = mix(baseSeed, canvas.width) % canvas.width;");
        script.AppendLine("            const py = mix(baseSeed, canvas.height) % canvas.height;");
        script.AppendLine("            let originalPixel;");
        script.AppendLine("            try {");
        script.AppendLine("                originalPixel = ctx.getImageData(px, py, 1, 1);");
        script.AppendLine("                const patched = ctx.getImageData(px, py, 1, 1);");
        script.AppendLine("                const delta = (mix(baseSeed, px ^ py) % 3) - 1;");
        script.AppendLine("                patched.data[0] = Math.min(255, Math.max(0, patched.data[0] + delta));");
        script.AppendLine("                ctx.putImageData(patched, px, py);");
        script.AppendLine("                return fn();");
        script.AppendLine("            } finally {");
        script.AppendLine("                try { if (originalPixel) ctx.putImageData(originalPixel, px, py); } catch (_) {}");
        script.AppendLine("            }");
        script.AppendLine("        };");
        script.AppendLine("        HTMLCanvasElement.prototype.toDataURL = function(type, quality) {");
        script.AppendLine("            return withStablePixelJitter(this, () => originalToDataURL.call(this, type, quality));");
        script.AppendLine("        };");
        script.AppendLine("        if (originalToBlob) {");
        script.AppendLine("            HTMLCanvasElement.prototype.toBlob = function(callback, type, quality) {");
        script.AppendLine("                return withStablePixelJitter(this, () => originalToBlob.call(this, callback, type, quality));");
        script.AppendLine("            };");
        script.AppendLine("        }");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 7. AudioContext fingerprint noise ===");
        script.AppendLine("    if (fp.audioNoise !== false) {");
        script.AppendLine("        try {");
        script.AppendLine("            const audioSeed = toSeedInt(fp.audioNoiseSeedHex || fp.userAgent);");
        script.AppendLine("            const touchedBuffers = new WeakMap();");
        script.AppendLine("            const originalGetChannelData = AudioBuffer.prototype.getChannelData;");
        script.AppendLine("            AudioBuffer.prototype.getChannelData = function(channel) {");
        script.AppendLine("                const data = originalGetChannelData.call(this, channel);");
        script.AppendLine("                let touched = touchedBuffers.get(this);");
        script.AppendLine("                if (!touched) {");
        script.AppendLine("                    touched = new Set();");
        script.AppendLine("                    touchedBuffers.set(this, touched);");
        script.AppendLine("                }");
        script.AppendLine("                if (!touched.has(channel)) {");
        script.AppendLine("                    touched.add(channel);");
        script.AppendLine("                    const jitter = ((mix(audioSeed, channel) % 21) - 10) / 1e7;");
        script.AppendLine("                    for (let i = 0; i < data.length; i += 997) {");
        script.AppendLine("                        data[i] += jitter;");
        script.AppendLine("                    }");
        script.AppendLine("                }");
        script.AppendLine("                return data;");
        script.AppendLine("            };");
        script.AppendLine("        } catch(e) {}");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    // === 8. Client Hints (Sec-CH-UA) ===");
        script.AppendLine("    if (fp.userAgent && fp.userAgent.includes('Chrome/')) {");
        script.AppendLine("        const match = fp.userAgent.match(/Chrome\\/([\\d.]+)/);");
        script.AppendLine("        const chromeVersion = match ? match[1].split('.')[0] : '125';");
        script.AppendLine("        const brands = [");
        script.AppendLine("            { brand: 'Chromium', version: chromeVersion },");
        script.AppendLine("            { brand: 'Not_A Brand', version: '8' },");
        script.AppendLine("            { brand: 'Google Chrome', version: chromeVersion }");
        script.AppendLine("        ];");
        script.AppendLine("        if (navigator.userAgentData) {");
        script.AppendLine("            Object.defineProperty(navigator.userAgentData, 'brands', {");
        script.AppendLine("                get: () => brands,");
        script.AppendLine("                configurable: true");
        script.AppendLine("            });");
        script.AppendLine("        }");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("})();");

        return script.ToString();
    }

    /// <summary>
    /// Скрипт для установки размера окна (выполняется после загрузки страницы).
    /// </summary>
    public static string GetViewportResizeScript(int width, int height)
    {
        return $@"
(function() {{
    try {{
        window.resizeTo({width}, {height});
        document.documentElement.style.width = '{width}px';
        document.documentElement.style.height = '{height}px';
    }} catch(e) {{}}
}})();";
    }
}
