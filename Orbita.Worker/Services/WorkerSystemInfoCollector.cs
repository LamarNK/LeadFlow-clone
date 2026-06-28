using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace Orbita.Worker.Services;

public sealed class WorkerSystemInfoCollector(IHttpClientFactory httpClientFactory)
{
    private static readonly DateTime ProcessStartedAtUtc = GetProcessStartUtc();
    private static readonly string OperatingSystemName = GetOperatingSystemName();

    private readonly object _ipLock = new();
    private string? _cachedPublicIp;
    private DateTime _publicIpCachedAtUtc = DateTime.MinValue;

    public DateTime StartedAtUtc => ProcessStartedAtUtc;

    public string OperatingSystem => OperatingSystemName;

    public async Task<string?> ResolvePublicIpAsync(CancellationToken ct)
    {
        lock (_ipLock)
        {
            if (_cachedPublicIp is not null && DateTime.UtcNow - _publicIpCachedAtUtc < TimeSpan.FromHours(24))
            {
                return _cachedPublicIp;
            }
        }

        var ip = await TryFetchPublicIpAsync(ct).ConfigureAwait(false);
        if (ip is null)
        {
            return GetCachedPublicIp();
        }

        lock (_ipLock)
        {
            _cachedPublicIp = ip;
            _publicIpCachedAtUtc = DateTime.UtcNow;
        }

        return ip;
    }

    private string? GetCachedPublicIp()
    {
        lock (_ipLock)
        {
            return _cachedPublicIp;
        }
    }

    private async Task<string?> TryFetchPublicIpAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(WorkerSystemInfoCollector));
        client.Timeout = TimeSpan.FromSeconds(8);

        string[] endpoints =
        [
            "https://api.ipify.org",
            "https://icanhazip.com",
            "https://ifconfig.me/ip"
        ];

        foreach (var endpoint in endpoints)
        {
            try
            {
                var response = await client.GetAsync(endpoint, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(body, out _))
                {
                    return body;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next endpoint.
            }
        }

        return null;
    }

    private static DateTime GetProcessStartUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    private static string GetOperatingSystemName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var caption = obj["Caption"]?.ToString()?.Trim();
                    var version = obj["Version"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        return string.IsNullOrWhiteSpace(version)
                            ? caption
                            : $"{caption} ({version})";
                    }
                }
            }
            catch
            {
                // Fall back to runtime description.
            }
        }

        return RuntimeInformation.OSDescription;
    }
}