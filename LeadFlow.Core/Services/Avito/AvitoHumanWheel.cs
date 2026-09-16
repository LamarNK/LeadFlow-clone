using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// «Человеческая» прокрутка колесом мыши через CDP <c>Input.dispatchMouseEvent(mouseWheel)</c>:
/// trusted wheel-события + серия мелких тиков с рандомными паузами вместо мгновенного JS scrollBy.
/// </summary>
internal static class AvitoHumanWheel
{
    /// <summary>
    /// Прокрутить основной скроллер страницы откликов. Возвращает true, если жест отправлен
    /// (факт движения проверяет вызывающий через probe scrollTop).
    /// </summary>
    public static async Task<bool> ScrollAsync(IPage page, int deltaPx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (deltaPx == 0)
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = await LocateCandidatesScrollerAnchorAsync(page, cancellationToken).ConfigureAwait(false);
            if (anchor is null)
            {
                return false;
            }

            return await ScrollOverRectAsync(page, anchor.X, anchor.Y, anchor.Width, anchor.Height, deltaPx, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Прокрутить колесом над заданным прямоугольником (rect в координатах вьюпорта).</summary>
    public static async Task<bool> ScrollOverRectAsync(
        IPage page,
        decimal x,
        decimal y,
        decimal width,
        decimal height,
        int deltaPx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (deltaPx == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Точка над скроллером — не центр-в-центр: как рука лежит, с рандомом.
            var mouseX = x + width * (decimal)(0.2 + Random.Shared.NextDouble() * 0.6);
            var mouseY = y + height * (decimal)(0.2 + Random.Shared.NextDouble() * 0.6);
            var viewportWidth = await TryReadViewportSizeAsync(page, "window.innerWidth").ConfigureAwait(false);
            var viewportHeight = await TryReadViewportSizeAsync(page, "window.innerHeight").ConfigureAwait(false);
            if (viewportWidth is > 1)
            {
                mouseX = Math.Clamp(mouseX, 1, viewportWidth.Value - 1);
            }
            if (viewportHeight is > 1)
            {
                mouseY = Math.Clamp(mouseY, 1, viewportHeight.Value - 1);
            }

            // Подвести мышь к точке (даёт mouseMoved перед wheel — как у человека).
            await page.Mouse.MoveAsync(
                    mouseX,
                    mouseY,
                    new MoveOptions { Steps = Random.Shared.Next(4, 10) })
                .ConfigureAwait(false);
            AvitoHumanPointer.NotePosition(page, mouseX, mouseY);
            await HumanDelay.DelayAsync(30, 110, cancellationToken).ConfigureAwait(false);

            var remaining = Math.Abs(deltaPx);
            var direction = deltaPx < 0 ? -1 : 1;
            while (remaining > 0)
            {
                var tick = Math.Min(
                    remaining,
                    AvitoHumanVariation.NextInclusive(
                        MonitoringTiming.WheelTickDeltaMinPx,
                        MonitoringTiming.WheelTickDeltaMaxPx));
                await DispatchWheelAsync(page, mouseX, mouseY, direction * tick).ConfigureAwait(false);
                dispatched = true;
                remaining -= tick;

                if (remaining > 0)
                {
                    await HumanDelay.DelayAsync(
                            MonitoringTiming.WheelTickPauseMinMs,
                            MonitoringTiming.WheelTickPauseMaxMs,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Иногда — микро-тик в обратную сторону, как поправка при недоскролле.
            if (AvitoHumanVariation.RollPermille(MonitoringTiming.WheelReverseTickChancePermille))
            {
                await HumanDelay.DelayAsync(40, 120, cancellationToken).ConfigureAwait(false);
                var correction = AvitoHumanVariation.NextInclusive(20, 60);
                await DispatchWheelAsync(page, mouseX, mouseY, -direction * correction).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A partial gesture may already have scrolled the page; do not send a second JS scroll.
            return dispatched;
        }
    }

    private static Task DispatchWheelAsync(IPage page, decimal x, decimal y, int deltaY) =>
        page.Client.SendAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseWheel",
            x,
            y,
            deltaX = 0,
            deltaY,
            modifiers = 0
        });

    private static async Task<int?> TryReadViewportSizeAsync(IPage page, string expression)
    {
        try
        {
            var value = await page.EvaluateExpressionAsync<int>(expression).ConfigureAwait(false);
            return value > 1 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Прямоугольник скроллера списка откликов в координатах вьюпорта.</summary>
    public sealed record ScrollerAnchor(decimal X, decimal Y, decimal Width, decimal Height);

    private static async Task<ScrollerAnchor?> LocateCandidatesScrollerAnchorAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await page.EvaluateFunctionAsync<string>(
                    @"() => {
                        const first = document.querySelector('[data-marker=""job-application/item""]');
                        let node = first ? first.parentElement : null;
                        while (node && node !== document.body) {
                            const style = window.getComputedStyle(node);
                            const overflowY = style.overflowY;
                            if ((overflowY === 'auto' || overflowY === 'scroll') && node.scrollHeight > node.clientHeight + 40) {
                                const rect = node.getBoundingClientRect();
                                return JSON.stringify({ x: rect.x, y: rect.y, width: rect.width, height: rect.height });
                            }
                            node = node.parentElement;
                        }

                        const hints = document.querySelector(""[class*='scrollable'], [data-marker='job-applications/list'], .styles-page-cyvKh, main"");
                        node = hints;
                        while (node && node !== document.body) {
                            const style = window.getComputedStyle(node);
                            if ((style.overflowY === 'auto' || style.overflowY === 'scroll') && node.scrollHeight > node.clientHeight + 40) {
                                const rect = node.getBoundingClientRect();
                                return JSON.stringify({ x: rect.x, y: rect.y, width: rect.width, height: rect.height });
                            }
                            node = node.parentElement;
                        }

                        return JSON.stringify({ x: 0, y: 0, width: window.innerWidth, height: window.innerHeight });
                    }")
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new ScrollerAnchor(
                root.GetProperty("x").GetDecimal(),
                root.GetProperty("y").GetDecimal(),
                root.GetProperty("width").GetDecimal(),
                root.GetProperty("height").GetDecimal());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
