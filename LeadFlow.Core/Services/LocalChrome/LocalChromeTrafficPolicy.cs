using System.Diagnostics;
using LeadFlow.Core.Models;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Request interception только на рабочей вкладке Local Chrome во время мониторинга.
/// Ручной «Открыть браузер» политику не включает.
/// </summary>
public sealed class LocalChromeTrafficPolicy : IAsyncDisposable
{
    private static readonly AsyncLocal<LocalChromeTrafficPolicy?> CurrentPolicy = new();
    private static readonly AsyncLocal<int> CaptchaImageAllowDepth = new();

    private readonly LocalChromeTrafficSettings _settings;
    private readonly AvitoAccount? _account;
    private readonly Stopwatch _firstNavigation = new();
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
    private int _disposed;
    private LocalChromeTrafficPolicy? _previous;

    private LocalChromeTrafficPolicy(LocalChromeTrafficSettings settings, AvitoAccount? account)
    {
        _settings = settings;
        _account = account;
    }

    public LocalChromeTrafficSettings Settings => _settings;

    public bool IsMonitoringSession { get; private init; }

    public static bool IsMonitoringAttached => CurrentPolicy.Value is { IsMonitoringSession: true, _disposed: 0 };

    public static int ResolveNavigationTimeoutMs(int fallbackMs)
    {
        var current = CurrentPolicy.Value;
        if (current is not { IsMonitoringSession: true, _disposed: 0 })
        {
            return fallbackMs;
        }

        return current._settings.NavigationTimeoutSeconds * 1000;
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
        var policy = new LocalChromeTrafficPolicy(settings, account) { IsMonitoringSession = true };
        policy._previous = CurrentPolicy.Value;
        CurrentPolicy.Value = policy;
        return policy;
    }

    public static LocalChromeTrafficPolicy None { get; } = new(LocalChromeTrafficRules.Disabled, account: null);

    public static IDisposable AllowImages()
    {
        CaptchaImageAllowDepth.Value++;
        return new CaptchaScope();
    }

    public static async Task TryAttachCurrentAsync(IPage page, CancellationToken cancellationToken = default)
    {
        var current = CurrentPolicy.Value;
        if (current is not { IsMonitoringSession: true, _disposed: 0 })
        {
            return;
        }

        await current.AttachAsync(page, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachAsync(IPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_disposed != 0 || !IsMonitoringSession)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _page = page;
        try
        {
            page.DefaultNavigationTimeout = _settings.NavigationTimeoutSeconds * 1000;
        }
        catch
        {
            // Default timeout is best-effort.
        }

        _requestHandler = OnRequest;
        _loadHandler = OnLoad;
        page.Request += _requestHandler;
        page.Load += _loadHandler;
        try
        {
            await page.SetRequestInterceptionAsync(true).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            page.Request -= _requestHandler;
            page.Load -= _loadHandler;
            _requestHandler = null;
            _loadHandler = null;
            _page = null;
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

        var page = _page;
        var requestHandler = _requestHandler;
        var loadHandler = _loadHandler;
        _page = null;
        _requestHandler = null;
        _loadHandler = null;
        if (page is not null)
        {
            if (requestHandler is not null)
            {
                page.Request -= requestHandler;
            }

            if (loadHandler is not null)
            {
                page.Load -= loadHandler;
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

        if (_account is not null)
        {
            _account.LocalTrafficLastStats = Snapshot();
        }

        if (ReferenceEquals(CurrentPolicy.Value, this))
        {
            CurrentPolicy.Value = _previous;
        }
    }

    private async void OnRequest(object? sender, RequestEventArgs e)
    {
        var request = e.Request;
        try
        {
            NoteFirstNavigationStart(request);
            var kind = LocalChromeTrafficRules.Classify(
                _settings,
                request.ResourceType.ToString(),
                request.Url,
                Header(request, "purpose") ?? Header(request, "Purpose"),
                Header(request, "sec-purpose") ?? Header(request, "Sec-Purpose"),
                CaptchaImageAllowDepth.Value > 0);
            if (kind == LocalChromeTrafficBlockKind.None)
            {
                await request.ContinueAsync().ConfigureAwait(false);
                return;
            }

            Increment(kind);
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

    private sealed class CaptchaScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var depth = CaptchaImageAllowDepth.Value;
            CaptchaImageAllowDepth.Value = depth > 0 ? depth - 1 : 0;
        }
    }
}
