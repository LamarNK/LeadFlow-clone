using System.Diagnostics;
using System.Runtime.CompilerServices;
using LeadFlow.Core.Models;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Request interception только на рабочей вкладке Local Chrome во время мониторинга.
/// Привязана к конкретной <see cref="IPage"/>, а не к AsyncLocal: события Puppeteer
/// и капча живут в другом execution context.
/// Ручной «Открыть браузер» политику не включает.
/// </summary>
public sealed class LocalChromeTrafficPolicy : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IPage, LocalChromeTrafficPolicy> Pages = new();

    private readonly LocalChromeTrafficSettings _settings;
    private readonly AvitoAccount? _account;
    private readonly Stopwatch _firstNavigation = new();
    private readonly object _sync = new();
    private IPage? _page;
    private EventHandler<RequestEventArgs>? _requestHandler;
    private EventHandler? _loadHandler;
    private int _firstNavigationStarted;
    private int _firstNavigationMs = -1;
    private int _blockedMedia;
    private int _blockedImages;
    private int _blockedFonts;
    private int _blockedAnalytics;
    private int _blockedPrefetch;
    private int _captchaImageAllowDepth;
    private int _disposed;

    private LocalChromeTrafficPolicy(LocalChromeTrafficSettings settings, AvitoAccount? account)
    {
        _settings = settings;
        _account = account;
    }

    public LocalChromeTrafficSettings Settings => _settings;

    public bool IsMonitoringSession { get; private init; }

    public bool CaptchaImagesAllowed => Volatile.Read(ref _captchaImageAllowDepth) > 0;

    public static LocalChromeTrafficPolicy? ForPage(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Pages.TryGetValue(page, out var policy) && Volatile.Read(ref policy._disposed) == 0
            ? policy
            : null;
    }

    public static int ResolveNavigationTimeoutMs(IPage? page, int fallbackMs)
    {
        if (page is null)
        {
            return fallbackMs;
        }

        var policy = ForPage(page);
        if (policy is not { IsMonitoringSession: true })
        {
            return fallbackMs;
        }

        return policy._settings.NavigationTimeoutSeconds * 1000;
    }

    public static LocalChromeTrafficPolicy BeginMonitoring(AvitoAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var settings = LocalChromeTrafficRules.FromStored(
            account.LocalTrafficMode,
            account.LocalBlockMedia,
            account.LocalBlockAnalytics,
            account.LocalBlockImages,
            account.LocalBlockFonts,
            account.LocalBlockPrefetch,
            account.LocalNavigationTimeoutSeconds);
        return new LocalChromeTrafficPolicy(settings, account) { IsMonitoringSession = true };
    }

    public static LocalChromeTrafficPolicy None { get; } = new(LocalChromeTrafficRules.Disabled, account: null);

    public static IDisposable AllowImages(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var policy = ForPage(page);
        if (policy is null)
        {
            return NoopScope.Instance;
        }

        Interlocked.Increment(ref policy._captchaImageAllowDepth);
        return new CaptchaScope(policy);
    }

    public async Task AttachAsync(IPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Volatile.Read(ref _disposed) != 0 || !IsMonitoringSession)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsBoundTo(page) && _requestHandler is not null)
        {
            TrySetNavigationTimeout(page);
            return;
        }

        await DetachPageAsync(disableInterception: true).ConfigureAwait(false);
        Bind(page);
        TrySetNavigationTimeout(page);

        var requestHandler = new EventHandler<RequestEventArgs>(OnRequest);
        var loadHandler = new EventHandler(OnLoad);
        _requestHandler = requestHandler;
        _loadHandler = loadHandler;
        page.Request += requestHandler;
        page.Load += loadHandler;
        try
        {
            await page.SetRequestInterceptionAsync(true).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            page.Request -= requestHandler;
            page.Load -= loadHandler;
            if (ReferenceEquals(_requestHandler, requestHandler))
            {
                _requestHandler = null;
            }

            if (ReferenceEquals(_loadHandler, loadHandler))
            {
                _loadHandler = null;
            }
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            await DetachPageAsync(disableInterception: true).ConfigureAwait(false);
        }
    }

    public LocalChromeTrafficLastStats Snapshot() =>
        new(
            _firstNavigationMs >= 0 ? _firstNavigationMs : null,
            Volatile.Read(ref _blockedMedia),
            Volatile.Read(ref _blockedImages),
            Volatile.Read(ref _blockedFonts),
            Volatile.Read(ref _blockedAnalytics),
            Volatile.Read(ref _blockedPrefetch));

    public LocalChromeTrafficBlockKind Classify(
        string? resourceType,
        string? url,
        string? purposeHeader = null,
        string? secPurposeHeader = null)
    {
        var kind = LocalChromeTrafficRules.Classify(
            _settings,
            resourceType,
            url,
            purposeHeader,
            secPurposeHeader,
            CaptchaImagesAllowed);
        if (kind != LocalChromeTrafficBlockKind.None)
        {
            Increment(kind);
        }

        return kind;
    }

    public static LocalChromeTrafficBlockKind Classify(
        LocalChromeTrafficSettings settings,
        IRequest request,
        bool captchaActive)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        return LocalChromeTrafficRules.Classify(
            settings,
            request.ResourceType.ToString(),
            request.Url,
            Header(request, "Purpose"),
            Header(request, "Sec-Purpose") ?? Header(request, "Sec-Purpose".ToLowerInvariant()),
            captchaActive);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DetachPageAsync(disableInterception: true).ConfigureAwait(false);
        if (_account is not null)
        {
            _account.LocalTrafficLastStats = Snapshot();
        }
    }

    private bool IsBoundTo(IPage page)
    {
        lock (_sync)
        {
            return ReferenceEquals(_page, page);
        }
    }

    private void Bind(IPage page)
    {
        lock (_sync)
        {
            var previous = _page;
            if (previous is not null && !ReferenceEquals(previous, page))
            {
                RemoveMapping(previous);
            }

            _page = page;
            Pages.AddOrUpdate(page, this);
        }
    }

    private async Task DetachPageAsync(bool disableInterception)
    {
        IPage? page;
        EventHandler<RequestEventArgs>? requestHandler;
        EventHandler? loadHandler;
        lock (_sync)
        {
            page = _page;
            requestHandler = _requestHandler;
            loadHandler = _loadHandler;
            _page = null;
            _requestHandler = null;
            _loadHandler = null;
            if (page is not null)
            {
                RemoveMapping(page);
            }
        }

        if (page is null)
        {
            return;
        }

        if (requestHandler is not null)
        {
            page.Request -= requestHandler;
        }

        if (loadHandler is not null)
        {
            page.Load -= loadHandler;
        }

        if (!disableInterception)
        {
            return;
        }

        try
        {
            await page.SetRequestInterceptionAsync(false).ConfigureAwait(false);
        }
        catch
        {
            // Detach must never throw.
        }
    }

    private void RemoveMapping(IPage page)
    {
        if (Pages.TryGetValue(page, out var mapped) && ReferenceEquals(mapped, this))
        {
            Pages.Remove(page);
        }
    }

    private void TrySetNavigationTimeout(IPage page)
    {
        try
        {
            page.DefaultNavigationTimeout = _settings.NavigationTimeoutSeconds * 1000;
        }
        catch
        {
            // Default timeout is best-effort.
        }
    }

    private async void OnRequest(object? sender, RequestEventArgs e)
    {
        var request = e.Request;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                await request.ContinueAsync().ConfigureAwait(false);
                return;
            }

            NoteFirstNavigationStart(request);
            var kind = Classify(
                request.ResourceType.ToString(),
                request.Url,
                Header(request, "purpose") ?? Header(request, "Purpose"),
                Header(request, "sec-purpose") ?? Header(request, "Sec-Purpose"));
            if (kind == LocalChromeTrafficBlockKind.None)
            {
                await request.ContinueAsync().ConfigureAwait(false);
                return;
            }

            await request.AbortAsync().ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await request.ContinueAsync().ConfigureAwait(false);
            }
            catch
            {
                // Request is already handled or the page is gone.
            }
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            if (!_firstNavigation.IsRunning)
            {
                return;
            }

            _firstNavigation.Stop();
            Interlocked.Exchange(ref _firstNavigationMs, (int)Math.Min(int.MaxValue, _firstNavigation.ElapsedMilliseconds));
        }
        catch
        {
            // Stats must never break monitoring.
        }
    }

    private void NoteFirstNavigationStart(IRequest request)
    {
        if (request.ResourceType != ResourceType.Document)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _firstNavigationStarted, 1, 0) != 0)
        {
            return;
        }

        _firstNavigation.Restart();
    }

    private void Increment(LocalChromeTrafficBlockKind kind)
    {
        switch (kind)
        {
            case LocalChromeTrafficBlockKind.Media:
                Interlocked.Increment(ref _blockedMedia);
                break;
            case LocalChromeTrafficBlockKind.Image:
                Interlocked.Increment(ref _blockedImages);
                break;
            case LocalChromeTrafficBlockKind.Font:
                Interlocked.Increment(ref _blockedFonts);
                break;
            case LocalChromeTrafficBlockKind.Analytics:
                Interlocked.Increment(ref _blockedAnalytics);
                break;
            case LocalChromeTrafficBlockKind.Prefetch:
                Interlocked.Increment(ref _blockedPrefetch);
                break;
        }
    }

    private static string? Header(IRequest request, string name)
    {
        try
        {
            var headers = request.Headers;
            if (headers is null || headers.Count == 0)
            {
                return null;
            }

            if (headers.TryGetValue(name, out var value))
            {
                return value;
            }

            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed class CaptchaScope(LocalChromeTrafficPolicy policy) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Interlocked.Decrement(ref policy._captchaImageAllowDepth) < 0)
            {
                Interlocked.Exchange(ref policy._captchaImageAllowDepth, 0);
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
