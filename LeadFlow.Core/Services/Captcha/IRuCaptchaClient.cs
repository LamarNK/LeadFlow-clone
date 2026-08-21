namespace LeadFlow.Core.Services.Captcha;

public interface IRuCaptchaClient
{
    Task<GeeTestV4Solution> SolveGeeTestV4Async(
        string apiKey,
        string websiteUrl,
        string captchaId,
        CancellationToken cancellationToken = default);
}
