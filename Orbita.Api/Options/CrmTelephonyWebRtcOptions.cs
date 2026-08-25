namespace Orbita.Api.Options;

public sealed class CrmTelephonyWebRtcOptions
{
    public const string SectionName = "TelephonyWebRtc";

    public bool Enabled { get; set; }
    public string WebSocketUrl { get; set; } = string.Empty;
    public string SipDomain { get; set; } = string.Empty;
    public string IceServerUrls { get; set; } = "stun:stun.l.google.com:19302";
    public string IceUsername { get; set; } = string.Empty;
    public string IceCredential { get; set; } = string.Empty;
    public string IceAuthSecret { get; set; } = string.Empty;
    public int IceCredentialTtlSeconds { get; set; } = 3600;
}
