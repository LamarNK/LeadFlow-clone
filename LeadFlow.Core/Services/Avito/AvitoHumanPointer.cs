using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// CDP-клик мышью со сдвигом от центра и промежуточным Move — isTrusted=true,
/// в отличие от <c>element.click()</c> / синтетического MouseEvent.
/// </summary>
internal static class AvitoHumanPointer
{
    public static async Task<bool> TryClickHandleAsync(
        IPage page,
        IElementHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(handle);

        try
        {
            await handle.EvaluateFunctionAsync(
                    "el => { try { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {} }")
                .ConfigureAwait(false);
            await HumanDelay.DelayAsync(70, 180, cancellationToken).ConfigureAwait(false);

            var box = await handle.BoundingBoxAsync().ConfigureAwait(false);
            if (box is null || box.Width < 1 || box.Height < 1)
            {
                return false;
            }

            var x = box.X + box.Width * (decimal)(0.32 + Random.Shared.NextDouble() * 0.36);
            var y = box.Y + box.Height * (decimal)(0.32 + Random.Shared.NextDouble() * 0.36);
            await page.Mouse.MoveAsync(x, y, new MoveOptions { Steps = Random.Shared.Next(5, 14) })
                .ConfigureAwait(false);
            await HumanDelay.DelayAsync(25, 80, cancellationToken).ConfigureAwait(false);
            await page.Mouse.ClickAsync(x, y, new ClickOptions { Delay = Random.Shared.Next(35, 90) })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryClickSelectorAsync(
        IPage page,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        try
        {
            var handle = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
            if (handle is null)
            {
                return false;
            }

            return await TryClickHandleAsync(page, handle, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryTypeIntoHandleAsync(
        IPage page,
        IElementHandle handle,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(handle);
        text ??= string.Empty;

        if (!await TryClickHandleAsync(page, handle, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await HumanDelay.DelayAsync(120, 280, cancellationToken).ConfigureAwait(false);
        try
        {
            await page.Keyboard.DownAsync("Control").ConfigureAwait(false);
            await page.Keyboard.PressAsync("KeyA").ConfigureAwait(false);
            await page.Keyboard.UpAsync("Control").ConfigureAwait(false);
            await page.Keyboard.PressAsync("Backspace").ConfigureAwait(false);
            await handle.TypeAsync(text, new TypeOptions { Delay = HumanDelay.NextTypeCharDelayMs() })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
