using System.Runtime.CompilerServices;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// CDP-клик мышью со сдвигом от центра и промежуточным Move — isTrusted=true,
/// в отличие от <c>element.click()</c> / синтетического MouseEvent.
/// </summary>
internal static class AvitoHumanPointer
{
    /// <summary>Последняя известная позиция мыши на странице (для старта кривых траекторий).</summary>
    private static readonly ConditionalWeakTable<IPage, StrongBox<AvitoMousePath.PathPoint>> LastMousePositions = new();

    private static AvitoMousePath.PathPoint GetLastMousePosition(IPage page)
    {
        return LastMousePositions.TryGetValue(page, out var box) ? box.Value : default;
    }

    internal static bool TryGetLastPosition(IPage page, out AvitoMousePath.PathPoint position)
    {
        if (LastMousePositions.TryGetValue(page, out var box))
        {
            position = box.Value;
            return true;
        }

        position = default;
        return false;
    }

    private static void SetLastMousePosition(IPage page, decimal x, decimal y)
    {
        var box = LastMousePositions.GetOrCreateValue(page);
        box.Value = new AvitoMousePath.PathPoint(x, y);
    }

    internal static void NotePosition(IPage page, decimal x, decimal y) => SetLastMousePosition(page, x, y);

    /// <summary>Движение по кривой: вейпоинты AvitoMousePath, между ними — Move с 3–9 шагами и микропаузами.</summary>
    private static async Task MoveAlongPathAsync(
        IPage page,
        decimal targetX,
        decimal targetY,
        CancellationToken cancellationToken)
    {
        var viewportWidth = await TryReadViewportWidthAsync(page).ConfigureAwait(false);
        var viewportHeight = await TryReadViewportHeightAsync(page).ConfigureAwait(false);
        var maxX = viewportWidth > 1 ? viewportWidth - 1 : int.MaxValue;
        var maxY = viewportHeight > 1 ? viewportHeight - 1 : int.MaxValue;
        targetX = Math.Clamp(targetX, 0, maxX);
        targetY = Math.Clamp(targetY, 0, maxY);

        var start = GetLastMousePosition(page);
        if (start == default)
        {
            await page.Mouse.MoveAsync(targetX, targetY, new MoveOptions { Steps = Random.Shared.Next(5, 12) })
                .ConfigureAwait(false);
            SetLastMousePosition(page, targetX, targetY);
            return;
        }

        var path = AvitoMousePath.BuildPath(start.X, start.Y, targetX, targetY);
        foreach (var point in path)
        {
            var pointX = Math.Clamp(point.X, 0, maxX);
            var pointY = Math.Clamp(point.Y, 0, maxY);
            await page.Mouse.MoveAsync(
                    pointX,
                    pointY,
                    new MoveOptions { Steps = Random.Shared.Next(3, 9) })
                .ConfigureAwait(false);
            await HumanDelay.DelayAsync(15, 45, cancellationToken).ConfigureAwait(false);
        }

        SetLastMousePosition(page, targetX, targetY);
    }

    public static async Task<bool> TryClickHandleAsync(
        IPage page,
        IElementHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(handle);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Не каждый элемент центрируем перед кликом: человек кликает и по краю вьюпорта.
            // Если элемент уже виден — с шансом 50% скролл пропускаем.
            var needsScroll = true;
            try
            {
                var preBox = await handle.BoundingBoxAsync().ConfigureAwait(false);
                if (preBox is { Width: >= 1, Height: >= 1 })
                {
                    var viewportHeight = await TryReadViewportHeightAsync(page).ConfigureAwait(false);
                    var viewportWidth = await TryReadViewportWidthAsync(page).ConfigureAwait(false);
                    if (viewportHeight > 0
                        && preBox.Y >= 12
                        && preBox.Y + preBox.Height <= viewportHeight - 12
                        && (viewportWidth <= 0 || (preBox.X >= 8 && preBox.X + preBox.Width <= viewportWidth - 8))
                        && !AvitoHumanVariation.RollPermille(500))
                    {
                        needsScroll = false;
                    }
                }
            }
            catch
            {
                // Не смогли снять геометрию — скроллим как раньше.
            }

            if (needsScroll)
            {
                await handle.EvaluateFunctionAsync(
                        "el => { try { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {} }")
                    .ConfigureAwait(false);
                await HumanDelay.DelayAsync(100, 320, cancellationToken).ConfigureAwait(false);
            }

            var box = await handle.BoundingBoxAsync().ConfigureAwait(false);
            if (box is null || box.Width < 1 || box.Height < 1)
            {
                return false;
            }

            var x = box.X + box.Width * (decimal)(0.28 + Random.Shared.NextDouble() * 0.44);
            var y = box.Y + box.Height * (decimal)(0.28 + Random.Shared.NextDouble() * 0.44);
            if (AvitoHumanVariation.RollPermille(MonitoringTiming.MouseWanderChancePermille))
            {
                var wanderX = box.X + box.Width * (decimal)Random.Shared.NextDouble();
                var wanderY = box.Y - (decimal)Random.Shared.Next(24, 120);
                await MoveAlongPathAsync(page, wanderX, wanderY, cancellationToken).ConfigureAwait(false);
                await HumanDelay.DelayAsync(60, 260, cancellationToken).ConfigureAwait(false);
            }

            // Основное движение — по кривой (безье-подобные вейпоинты), с шансом overshoot+коррекция.
            if (AvitoHumanVariation.RollPermille(250))
            {
                var overshoot = AvitoMousePath.BuildOvershoot(
                    GetLastMousePosition(page).X,
                    GetLastMousePosition(page).Y,
                    x,
                    y);
                await MoveAlongPathAsync(page, overshoot.X, overshoot.Y, cancellationToken).ConfigureAwait(false);
                await HumanDelay.DelayAsync(60, 150, cancellationToken).ConfigureAwait(false);
                await MoveAlongPathAsync(page, x, y, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await MoveAlongPathAsync(page, x, y, cancellationToken).ConfigureAwait(false);
            }

            await HumanDelay.DelayAsync(40, 150, cancellationToken).ConfigureAwait(false);

            // Последняя возможная проверка отмены непосредственно перед диспетчеризацией клика.
            // Puppeteer-клик после отправки нельзя атомарно отменить, поэтому между этой проверкой
            // и ClickAsync нет намеренных задержек.
            cancellationToken.ThrowIfCancellationRequested();
            await page.Mouse.ClickAsync(x, y, new ClickOptions { Delay = Random.Shared.Next(50, 140) })
                .ConfigureAwait(false);
            return true;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CDP-клик по дочернему элементу карточки списка: <paramref name="itemsSelector"/> выбирает карточки,
    /// <paramref name="index"/> — нужную, <paramref name="childSelector"/> (null → сама карточка) — цель внутри.
    /// </summary>
    public static async Task<bool> TryClickItemChildAsync(
        IPage page,
        string itemsSelector,
        int index,
        string? childSelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrWhiteSpace(itemsSelector) || index < 0)
        {
            return false;
        }

        try
        {
            var items = await page.QuerySelectorAllAsync(itemsSelector).ConfigureAwait(false);
            if (items is null || index >= items.Length)
            {
                return false;
            }

            IElementHandle target = items[index];
            if (!string.IsNullOrWhiteSpace(childSelector))
            {
                var child = await target.QuerySelectorAsync(childSelector).ConfigureAwait(false);
                if (child is null)
                {
                    return false;
                }

                target = child;
            }

            return await TryClickHandleAsync(page, target, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Закрыть popup контактов trusted-способом: клик по крестику, затем Escape с клавиатуры.</summary>
    public static async Task<bool> TryCloseContactsPopupAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            if (await TryClickSelectorAsync(
                    page,
                    "[data-marker='job-application/response/contacts-popup/close']",
                    cancellationToken).ConfigureAwait(false))
            {
                await HumanDelay.DelayAsync(80, 220, cancellationToken).ConfigureAwait(false);
                if (!await IsContactsPopupOpenAsync(page).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Падаем до Escape.
        }

        try
        {
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await HumanDelay.DelayAsync(80, 220, cancellationToken).ConfigureAwait(false);
            return !await IsContactsPopupOpenAsync(page).ConfigureAwait(false);
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

    private static async Task<bool> IsContactsPopupOpenAsync(IPage page)
    {
        try
        {
            return await page.QuerySelectorAsync("[data-marker='job-application/response/contacts-popup/popup']")
                .ConfigureAwait(false) is not null;
        }
        catch
        {
            return true;
        }
    }

    private static async Task<int> TryReadViewportHeightAsync(IPage page)
    {
        try
        {
            var raw = await page.EvaluateExpressionAsync<int>("window.innerHeight").ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int> TryReadViewportWidthAsync(IPage page)
    {
        try
        {
            var raw = await page.EvaluateExpressionAsync<int>("window.innerWidth").ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return 0;
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

        await HumanDelay.DelayAsync(180, 450, cancellationToken).ConfigureAwait(false);
        try
        {
            await page.Keyboard.DownAsync("Control").ConfigureAwait(false);
            await page.Keyboard.PressAsync("KeyA").ConfigureAwait(false);
            await page.Keyboard.UpAsync("Control").ConfigureAwait(false);
            await page.Keyboard.PressAsync("Backspace").ConfigureAwait(false);
            await TypeLikeHumanAsync(page, text, cancellationToken).ConfigureAwait(false);
            return true;
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

    /// <summary>
    /// Посимвольный ввод «как человек»: рандомный темп, паузы на границах слов, изредка
    /// «раздумья» и опечатка с исправлением. Заменяет ровный TypeAsync(Delay).
    /// </summary>
    private static async Task TypeLikeHumanAsync(
        IPage page,
        string text,
        CancellationToken cancellationToken)
    {
        foreach (var ch in text)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await page.Keyboard.SendCharacterAsync(ch.ToString()).ConfigureAwait(false);

            var delay = HumanDelay.NextTypeCharDelayMs();
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                // Между словами/после знаков — заметно длиннее.
                delay += Random.Shared.Next(80, 400);
            }

            if (AvitoHumanVariation.RollPermille(50))
            {
                // «Придумываю фразу».
                delay += Random.Shared.Next(500, 2000);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
