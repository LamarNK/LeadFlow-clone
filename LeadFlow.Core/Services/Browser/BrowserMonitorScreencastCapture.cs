using System.Text.Json;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Browser;

public sealed class BrowserMonitorScreencastCapture : IAsyncDisposable
{
    private readonly ICDPSession _client;
    private readonly CancellationToken _cancellationToken;
    private readonly object _frameSync = new();
    private readonly TaskCompletionSource<bool> _firstFrameReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _latestJpeg;
    private int _disposed;

    private BrowserMonitorScreencastCapture(ICDPSession client, CancellationToken cancellationToken)
    {
        _client = client;
        _cancellationToken = cancellationToken;
    }

    public static async Task<BrowserMonitorScreencastCapture> StartAsync(
        IPage page,
        CancellationToken cancellationToken = default)
    {
        var client = await page.CreateCDPSessionAsync().ConfigureAwait(false);
        var capture = new BrowserMonitorScreencastCapture(client, cancellationToken);
        capture.Attach();

        try
        {
            await client.SendAsync("Page.startScreencast", new
            {
                format = "jpeg",
                quality = 65,
                maxWidth = CaptchaViewportDefaults.Width,
                maxHeight = CaptchaViewportDefaults.Height,
                everyNthFrame = 1
            }).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public byte[]? TryGetLatestJpeg()
    {
        lock (_frameSync)
        {
            return _latestJpeg;
        }
    }

    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(_firstFrameReceived.Task, timeoutTask).ConfigureAwait(false);
        return completedTask == _firstFrameReceived.Task;
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
    }

    private void Attach() => _client.MessageReceived += HandleMessageReceived;

    private void Detach() => _client.MessageReceived -= HandleMessageReceived;

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
            await _client.SendAsync("Page.screencastFrameAck", new { sessionId = ackSessionId })
                .ConfigureAwait(false);

            var imageBase64 = messageData.GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                return;
            }

            var jpegBytes = Convert.FromBase64String(imageBase64);
            if (jpegBytes.Length == 0)
            {
                return;
            }

            lock (_frameSync)
            {
                _latestJpeg = jpegBytes;
            }

            _firstFrameReceived.TrySetResult(true);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            // ignore during shutdown
        }
        catch
        {
            // best-effort frame ingest
        }
    }
}