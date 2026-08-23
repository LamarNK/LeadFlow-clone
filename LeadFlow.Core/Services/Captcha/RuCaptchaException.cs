namespace LeadFlow.Core.Services.Captcha;

public sealed class RuCaptchaException : Exception
{
    public RuCaptchaException(string message)
        : base(message)
    {
    }

    public RuCaptchaException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public string? ErrorCode { get; init; }
}
