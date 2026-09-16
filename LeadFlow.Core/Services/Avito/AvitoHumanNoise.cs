using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Небольшое движение курсора от последней известной позиции во время длинных пауз.
/// </summary>
internal static class AvitoHumanNoise
{
    /// <summary>
    /// Ошибки глотаем: необязательное движение не должно ломать проход.
    /// </summary>
    public static async Task MaybeDriftAsync(IPage page, int chancePermille, CancellationToken cancellationToken)
    {
        if (!AvitoHumanVariation.RollPermille(chancePermille)
            || !AvitoHumanPointer.TryGetLastPosition(page, out var start))
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var width = await TryReadInnerAsync(page, "window.innerWidth", 1280).ConfigureAwait(false);
            var height = await TryReadInnerAsync(page, "window.innerHeight", 800).ConfigureAwait(false);
            var toX = Math.Clamp(start.X + Random.Shared.Next(-70, 71), 8, Math.Max(8, width - 8));
            var toY = Math.Clamp(start.Y + Random.Shared.Next(-50, 51), 8, Math.Max(8, height - 8));

            var path = AvitoMousePath.BuildPath(start.X, start.Y, toX, toY);
            foreach (var point in path)
            {
                var pointX = Math.Clamp(point.X, 1, Math.Max(1, width - 1));
                var pointY = Math.Clamp(point.Y, 1, Math.Max(1, height - 1));
                await page.Mouse.MoveAsync(pointX, pointY, new MoveOptions { Steps = Random.Shared.Next(3, 8) })
                    .ConfigureAwait(false);
                await HumanDelay.DelayAsync(15, 40, cancellationToken).ConfigureAwait(false);
            }

            AvitoHumanPointer.NotePosition(page, toX, toY);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Шум best-effort: любые ошибки игнорируем.
        }
    }

    private static async Task<decimal> TryReadInnerAsync(IPage page, string expression, int fallback)
    {
        try
        {
            var raw = await page.EvaluateExpressionAsync<int>(expression).ConfigureAwait(false);
            return raw > 0 ? raw : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
