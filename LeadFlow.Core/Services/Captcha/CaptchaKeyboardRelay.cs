using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Captcha;

public static class CaptchaKeyboardRelay
{
    public static bool IsKeyboardEvent(string eventType) =>
        NormalizeEventType(eventType) is "keydown" or "keyup";

    public static async Task DispatchAsync(
        IPage page,
        CaptchaInputPayload input,
        CancellationToken cancellationToken = default)
    {
        var normalizedEventType = NormalizeEventType(input.EventType);
        var key = ResolveKey(input);
        if (key is null)
        {
            return;
        }

        await page.BringToFrontAsync().ConfigureAwait(false);
        await page.EvaluateFunctionAsync(
                "() => { window.focus(); document.body?.focus?.(); }")
            .ConfigureAwait(false);

        if (normalizedEventType == "keydown")
        {
            var options = new DownOptions();
            if (ShouldSetText(input))
            {
                options.Text = input.Key;
            }

            await page.Keyboard.DownAsync(key, options).ConfigureAwait(false);
            return;
        }

        await page.Keyboard.UpAsync(key).ConfigureAwait(false);
    }

    private static string NormalizeEventType(string eventType) =>
        eventType.Trim().ToLowerInvariant();

    private static bool ShouldSetText(CaptchaInputPayload input) =>
        !string.IsNullOrEmpty(input.Key)
        && input.Key.Length == 1
        && !input.CtrlKey
        && !input.AltKey
        && !input.MetaKey;

    private static string? ResolveKey(CaptchaInputPayload input)
    {
        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            return NormalizeKeyValue(input.Key, input.Code);
        }

        if (!string.IsNullOrWhiteSpace(input.Code))
        {
            return NormalizeCodeValue(input.Code);
        }

        return null;
    }

    private static string NormalizeKeyValue(string key, string? code) =>
        key switch
        {
            " " or "Spacebar" => "Space",
            "Esc" => "Escape",
            "OS" => "Meta",
            "Left" => "ArrowLeft",
            "Right" => "ArrowRight",
            "Up" => "ArrowUp",
            "Down" => "ArrowDown",
            "Del" => "Delete",
            "Apps" => "ContextMenu",
            _ when string.Equals(key, "Unidentified", StringComparison.OrdinalIgnoreCase)
                => NormalizeCodeValue(code),
            _ => key
        };

    private static string NormalizeCodeValue(string? code) =>
        code switch
        {
            null or "" => string.Empty,
            "Space" => "Space",
            "ShiftLeft" or "ShiftRight" => "Shift",
            "ControlLeft" or "ControlRight" => "Control",
            "AltLeft" or "AltRight" => "Alt",
            "MetaLeft" or "MetaRight" => "Meta",
            "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" => code,
            "Backspace" or "Delete" or "Enter" or "Escape" or "Tab" => code,
            "BracketLeft" => "[",
            "BracketRight" => "]",
            "Backquote" => "`",
            "Backslash" => "\\",
            "Comma" => ",",
            "Period" => ".",
            "Quote" => "'",
            "Semicolon" => ";",
            "Slash" => "/",
            "Minus" => "-",
            "Equal" => "=",
            _ when code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4
                => code[3].ToString().ToLowerInvariant(),
            _ when code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6
                => code[5].ToString(),
            _ => code
        };
}
