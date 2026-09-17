using LeadFlow.Core.Services.Avito.Session;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Trusted-действия над страницей откликов (CDP-указатель/клавиатура/колесо),
/// доступные <see cref="AvitoCandidatesListPreparer"/> без прямого доступа к <c>IPage</c>.
/// null-колбэки включают легаси-JS-путь (синтетические события) — для тестов и WebView-фолбэка.
/// </summary>
public sealed record CandidatesPageActors(
    Func<int, string?, CancellationToken, Task<bool>>? PointerClickItemChildAsync = null,
    Func<CancellationToken, Task<bool>>? CloseContactsPopupAsync = null,
    Func<int, CancellationToken, Task<bool>>? WheelScrollAsync = null)
{
    /// <summary>Селектор карточек списка откликов.</summary>
    public const string ItemsSelector = "[data-marker='job-application/item']";

    /// <summary>Клик CDP-указателем по карточке (childSelector = null) или её дочернему элементу.</summary>
    public async Task<bool> TryClickItemChildAsync(int index, string? childSelector, CancellationToken cancellationToken)
    {
        if (PointerClickItemChildAsync is null)
        {
            return false;
        }

        try
        {
            return await PointerClickItemChildAsync(index, childSelector, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AvitoSessionRestartRequiredException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Закрыть popup контактов trusted-способом; false — сделать JS-фолбэк.</summary>
    public async Task<bool> SafeCloseContactsPopupAsync(CancellationToken cancellationToken)
    {
        if (CloseContactsPopupAsync is null)
        {
            return false;
        }

        try
        {
            return await CloseContactsPopupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AvitoSessionRestartRequiredException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Прокрутить список колесом мыши (CDP mouseWheel); положительный px — вниз.</summary>
    public async Task<bool> TryWheelScrollAsync(int deltaPx, CancellationToken cancellationToken)
    {
        if (WheelScrollAsync is null)
        {
            return false;
        }

        try
        {
            return await WheelScrollAsync(deltaPx, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AvitoSessionRestartRequiredException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
