namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Read-only JS для вкладки «Активные»: бесконечная прокрутка карточек
/// <c>item-snippet/</c> и клик «далее», без кнопок управления объявлением.
/// </summary>
internal static class AvitoAdListPageScripts
{
    private const string FindScrollerJs = """
            const findScroller = (seed) => {
                let node = seed;
                while (node && node !== document.body) {
                    const style = window.getComputedStyle(node);
                    const overflowY = (style.overflowY || "").toLowerCase();
                    if ((overflowY === "auto" || overflowY === "scroll") && node.scrollHeight > node.clientHeight + 40) {
                        return node;
                    }
                    node = node.parentElement;
                }
                return document.scrollingElement || document.documentElement;
            };
            const collectSnippets = () => Array.from(document.querySelectorAll("[data-marker^='item-snippet/']"));
            const rootHasLoader = () => {
                const root = document.querySelector("#personal-items-root-element") || document.body;
                return !!root.querySelector("[class*='styles-loader'], [class*='style-loader']");
            };
            const snippetProbe = (items, scroller, extras) => {
                const atEnd = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 8;
                return Object.assign({
                    count: items.length,
                    first: items[0]?.getAttribute("data-marker") || "",
                    last: items.at(-1)?.getAttribute("data-marker") || "",
                    atEnd,
                    loader: rootHasLoader()
                }, extras);
            };
        """;

    /// <summary>Счёт карточек и положение скролла без движения страницы.</summary>
    public const string ProbeScript = $$"""
        (() => {
        {{FindScrollerJs}}
            const items = collectSnippets();
            const scroller = findScroller(items.at(-1) || document.querySelector("#personal-items-root-element"));
            return JSON.stringify(snippetProbe(items, scroller, { moved: false, loadMoreClicked: false }));
        })()
        """;

    /// <summary>
    /// Клик «Показать ещё» (если есть) и шаг вниз по контейнеру списка.
    /// Не трогает кнопки объявления и pagination next.
    /// </summary>
    public const string ScrollStepScript = $$"""
        (() => {
        {{FindScrollerJs}}
            const clickLoadMore = () => {
                const marked = document.querySelector(
                    "[data-marker='pagination-button(more)'], [data-marker='catalog-more'], [data-marker='load-more']"
                );
                if (marked
                    && marked.getAttribute("aria-disabled") !== "true"
                    && !marked.hasAttribute("disabled")) {
                    marked.click();
                    return true;
                }
                const nodes = Array.from(document.querySelectorAll("button, a, [role='button']"));
                const more = nodes.find((el) => {
                    const text = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
                    return /^(показать ещё|показать еще|загрузить ещё|загрузить еще|показать больше)$/i.test(text);
                });
                if (!more) return false;
                more.click();
                return true;
            };

            const items = collectSnippets();
            const scroller = findScroller(items.at(-1) || document.querySelector("#personal-items-root-element"));
            const loadMoreClicked = clickLoadMore();
            const beforeTop = scroller.scrollTop;
            try {
                items.at(-1)?.scrollIntoView({ block: "end", inline: "nearest" });
            } catch {}
            const ratio = 0.32 + Math.random() * 0.28;
            const delta = Math.max(Math.floor(scroller.clientHeight * ratio), 180);
            try {
                scroller.scrollBy({ top: delta, left: 0, behavior: "auto" });
            } catch {
                scroller.scrollBy(0, delta);
            }
            return JSON.stringify(snippetProbe(items, scroller, {
                moved: Math.abs(scroller.scrollTop - beforeTop) > 2,
                loadMoreClicked
            }));
        })()
        """;

    /// <summary>Геометрия скроллера «Активных»: для расчёта дельты CDP-колеса.</summary>
    public const string GeometryScript = $$"""
        (() => {
        {{FindScrollerJs}}
            const items = collectSnippets();
            const scroller = findScroller(items.at(-1) || document.querySelector("#personal-items-root-element"));
            const rect = scroller.getBoundingClientRect();
            return JSON.stringify({
                scrollTop: scroller.scrollTop,
                clientHeight: scroller.clientHeight,
                scrollHeight: scroller.scrollHeight,
                x: rect.x,
                y: rect.y,
                width: rect.width,
                height: rect.height
            });
        })()
        """;

    /// <summary>Есть ли кликабельная кнопка «Показать ещё» (сам клик делает C# CDP-указателем).</summary>
    public const string HasLoadMoreScript = """
        (() => {
            const marked = document.querySelector(
                "[data-marker='pagination-button(more)'], [data-marker='catalog-more'], [data-marker='load-more']"
            );
            if (marked
                && marked.getAttribute("aria-disabled") !== "true"
                && !marked.hasAttribute("disabled")) {
                return true;
            }

            const nodes = Array.from(document.querySelectorAll("button, a, [role='button']"));
            return nodes.some((el) => {
                const text = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
                return /^(показать ещё|показать еще|загрузить ещё|загрузить еще|показать больше)$/i.test(text);
            });
        })()
        """;

    /// <summary>Селекторы кнопки «Показать ещё» для CDP-клика (по порядку приоритета).</summary>
    public static readonly string[] LoadMoreSelectors =
    [
        "[data-marker='pagination-button(more)']",
        "[data-marker='catalog-more']",
        "[data-marker='load-more']"
    ];

    /// <summary>Селекторы кнопки «следующая страница» для CDP-клика.</summary>
    public static readonly string[] NextPageSelectors =
    [
        "[data-marker='pagination-button(next)']",
        "[data-marker='pagination/next']",
        "a[rel='next']"
    ];

    public const string HasNextPageScript = """
        (() => {
            const next = document.querySelector('[data-marker="pagination-button(next)"], [data-marker="pagination/next"], a[rel="next"]');
            if (!next) return false;
            if (next.getAttribute('aria-disabled') === 'true' || next.hasAttribute('disabled')) return false;
            const cls = (next.getAttribute('class') || '').toLowerCase();
            if (cls.includes('disabled')) return false;
            return true;
        })()
        """;

    public const string ClickNextPageScript = """
        (() => {
            const next = document.querySelector('[data-marker="pagination-button(next)"], [data-marker="pagination/next"], a[rel="next"]');
            if (!next) return false;
            if (next.getAttribute('aria-disabled') === 'true' || next.hasAttribute('disabled')) return false;
            next.click();
            return true;
        })()
        """;
}
