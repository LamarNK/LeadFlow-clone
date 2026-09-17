namespace Orbita.Contracts;

/// <summary>
/// Временные выключатели функциональности. Вернуть возможность — поставить true.
/// </summary>
public static class OrbitaFeatureToggles
{
    /// <summary>Вкладка «Балансы» и пополнения на сайте.</summary>
    public const bool BalancesEnabled = false;

    /// <summary>Вкладка «Объявления» на сайте и проход мониторинга объявлений у воркера.</summary>
    public const bool ListingsEnabled = false;
}
