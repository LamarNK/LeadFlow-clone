namespace LeadFlow.Core.Services.Captcha;

public static class CaptchaCoordinateMapper
{
    public static (int X, int Y) MapToRemote(
        double panelX,
        double panelY,
        double panelWidth,
        double panelHeight,
        int viewportWidth,
        int viewportHeight)
    {
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            return (0, 0);
        }

        var scaleX = viewportWidth / panelWidth;
        var scaleY = viewportHeight / panelHeight;
        var x = (int)Math.Round(panelX * scaleX, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(panelY * scaleY, MidpointRounding.AwayFromZero);
        x = Math.Clamp(x, 0, Math.Max(0, viewportWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, viewportHeight - 1));
        return (x, y);
    }
}