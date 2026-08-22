namespace LeadFlow.Core.Services.Captcha;

public interface IRuCaptchaClient
{
    Task<GeeTestV4Solution> SolveGeeTestV4Async(
        string apiKey,
        string websiteUrl,
        string captchaId,
        CancellationToken cancellationToken = default);

    Task<GeeTestV4Solution> SolveGeeTestV4Async(
        string apiKey,
        string websiteUrl,
        string captchaId,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken = default);

    Task<HCaptchaSolution> SolveHCaptchaAsync(
        string apiKey,
        string websiteUrl,
        string websiteKey,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken = default);

    Task<ImageCaptchaSolution> SolveImageToTextAsync(
        string apiKey,
        string imageBody,
        CancellationToken cancellationToken = default);
}
