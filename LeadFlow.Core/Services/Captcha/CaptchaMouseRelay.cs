using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public static class CaptchaMouseRelay
{
    public static async Task DispatchAsync(
        IPage page,
        string eventType,
        int x,
        int y,
        int button = 0,
        int buttons = 0,
        CancellationToken cancellationToken = default)
    {
        var type = MapEventType(eventType);
        var buttonMask = ResolveButtonsMask(type, button, buttons);
        var buttonName = ResolveButtonName(type, button, buttonMask);

        await page.Client.SendAsync("Input.dispatchMouseEvent", new
        {
            type,
            x,
            y,
            button = buttonName,
            buttons = buttonMask,
            clickCount = type == "mousePressed" ? 1 : 0
        }).ConfigureAwait(false);
    }

    private static string MapEventType(string eventType) =>
        eventType.ToLowerInvariant() switch
        {
            "mousedown" or "mousepressed" => "mousePressed",
            "mouseup" or "mousereleased" => "mouseReleased",
            _ => "mouseMoved"
        };

    private static string ResolveButtonName(string type, int button, int buttonMask)
    {
        if (type == "mouseMoved")
        {
            return ButtonMaskToName(buttonMask);
        }

        return button switch
        {
            1 => "middle",
            2 => "right",
            _ => "left"
        };
    }

    private static int ResolveButtonsMask(string type, int button, int buttons)
    {
        if (type == "mouseMoved")
        {
            return Math.Max(0, buttons);
        }

        if (type == "mouseReleased")
        {
            return 0;
        }

        return buttons > 0 ? buttons : ButtonToMask(button);
    }

    private static int ButtonToMask(int button) =>
        button switch
        {
            1 => 4,
            2 => 2,
            _ => 1
        };

    private static string ButtonMaskToName(int buttons)
    {
        if ((buttons & 1) != 0)
        {
            return "left";
        }

        if ((buttons & 2) != 0)
        {
            return "right";
        }

        if ((buttons & 4) != 0)
        {
            return "middle";
        }

        return "none";
    }
}
