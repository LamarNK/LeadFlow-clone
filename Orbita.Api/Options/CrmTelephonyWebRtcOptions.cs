namespace Orbita.Api.Options;

public sealed class CrmTelephonyWebRtcOptions
{
    public const string SectionName = "TelephonyWebRtc";

    public bool Enabled { get; set; }
    public string WebSocketUrl { get; set; } = string.Empty;
    public string SipDomain { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string AuthorizationUsername { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public Dictionary<string, CrmTelephonyWebRtcEndpointOptions> Endpoints { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public CrmTelephonyWebRtcEndpointOptions? ResolveEndpoint(string extension)
    {
        if (Endpoints.TryGetValue(extension, out var configured))
        {
            return configured;
        }

        return string.Equals(Extension.Trim(), extension, StringComparison.OrdinalIgnoreCase)
            ? new CrmTelephonyWebRtcEndpointOptions
            {
                AuthorizationUsername = AuthorizationUsername,
                Password = Password
            }
            : null;
    }
}

public sealed class CrmTelephonyWebRtcEndpointOptions
{
    public string AuthorizationUsername { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
