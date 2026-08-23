namespace Orbita.Api.Options;

public sealed class CrmTelephonyWebRtcOptions
{
    public const string SectionName = "TelephonyWebRtc";

    public bool Enabled { get; set; }
    public string WebSocketUrl { get; set; } = string.Empty;
    public string SipDomain { get; set; } = string.Empty;
}
