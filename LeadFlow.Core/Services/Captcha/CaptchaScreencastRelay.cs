using System.Text.Json;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

internal sealed class CaptchaScreencastRelay : IAsyncDisposable
{
    private readonly ICDPSession _client;
    private readonly Guid _sessionId;
    private readonly int _viewportWidth;
    private readonly int _viewportHeight;
    private readonly Func<CaptchaFramePayload, CancellationToken, Task> _onFrame;
    private readonly CancellationToken _cancellationToken;
    private readonly SemaphoreSlim _frameLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _firstFrameReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private CaptchaScreencastRelay(
        ICDPSession client,
        Guid sessionId,
        int viewportWidth,
        int viewportHeight,
        Func<CaptchaFramePayload, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        _client = client;
        _sessionId = sessionId;
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _onFrame = onFrame;
        _cancellationToken = cancellationToken;
    }

    public static async Task<CaptchaScreencastRelay> StartAsync(
        IPage page,
        Guid sessionId,
        int viewportWidth,
        int viewportHeight,
        Func<CaptchaFramePayload, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        await page.BringToFrontAsync().ConfigureAwait(false);
        await page.EvaluateFunctionAsync(
                "() => { window.focus(); document.body?.focus?.(); }")
            .ConfigureAwait(false);

        var client = await page.CreateCDPSessionAsync().ConfigureAwait(false);
        var relay = new CaptchaScreencastRelay(
            client,
            sessionId,
            viewportWidth,
            viewportHeight,
            onFrame,
            cancellationToken);

        relay.Attach();
        try
        {
            await client.SendAsync("Page.enable").ConfigureAwait(false);
            await client.SendAsync("Page.startScreencast", new
            {
                format = "jpeg",
                quality = 70,
                maxWidth = viewportWidth,
                maxHeight = viewportHeight,
                everyNthFrame = 1
            }).ConfigureAwait(false);
            return relay;
        }
        catch
        {
            await relay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(_firstFrameReceived.Task, _completion.Task, timeoutTask).ConfigureAwait(false);
        return completedTask == _firstFrameReceived.Task;
    }

    public async Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(_completion.Task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completedTask != _completion.Task)
        {
            return true;
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Detach();

        try
        {
            await _client.SendAsync("Page.stopScreencast").ConfigureAwait(false);
        }
        catch
        {
            // best-effort shutdown
        }

        try
        {
            await _client.DetachAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort shutdown
        }

        _frameLock.Dispose();
    }

    private void Attach()
    {
        _client.MessageReceived += HandleMessageReceived;
        _client.Disconnected += HandleDisconnected;
    }

    private void Detach()
    {
        _client.MessageReceived -= HandleMessageReceived;
        _client.Disconnected -= HandleDisconnected;
    }

    private void HandleDisconnected(object? sender, EventArgs e)
    {
        if (_cancellationToken.IsCancellationRequested || _disposed != 0)
        {
            _completion.TrySetResult(true);
            return;
        }

        _completion.TrySetResult(false);
    }

    private void HandleMessageReceived(object? sender, MessageEventArgs e)
    {
        if (!string.Equals(e.MessageID, "Page.screencastFrame", StringComparison.Ordinal))
        {
            return;
        }

        _ = ProcessFrameAsync(e.MessageData);
    }

    private async Task ProcessFrameAsync(JsonElement messageData)
    {
        try
        {
            var ackSessionId = messageData.GetProperty("sessionId").GetInt32();
            await _client.SendAsync("Page.screencastFrameAck", new { sessionId = ackSessionId }).ConfigureAwait(false);

            var imageBase64 = messageData.GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                return;
            }

            // A frame is available as soon as CDP delivers it. Do not make screencast startup depend on
            // SignalR delivery, which can be cancelled while the operator is completing the captcha.
            _firstFrameReceived.TrySetResult(true);

            await _frameLock.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                await _onFrame(
                        new CaptchaFramePayload(
                            _sessionId,
                            imageBase64,
                            _viewportWidth,
                            _viewportHeight,
                            ResolveTimestampMs(messageData)),
                        _cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _frameLock.Release();
            }
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _completion.TrySetResult(true);
        }
        catch
        {
            _completion.TrySetResult(false);
        }
    }

    private static long ResolveTimestampMs(JsonElement messageData)
    {
        if (messageData.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("timestamp", out var timestamp)
            && timestamp.ValueKind == JsonValueKind.Number
            && timestamp.TryGetDouble(out var seconds))
        {
            return (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
