using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Пытается восстановить сессию Avito через UI входа:
/// сохранённый профиль → пароль (Орбита/браузер); если профиля нет — полный логин/пароль.
/// </summary>
public static class AvitoAutoLoginRecovery
{
    private const string ProfileDashboardPageUrl = "https://www.avito.ru/profile/dashboard";

    public sealed record ProbeState(
        bool NeedsLogin,
        bool IsAuthorized,
        bool HasCaptcha,
        bool HasLoginForm,
        bool HasUsersList,
        bool HasSavedUserCard,
        bool HasOtherProfileLink,
        bool HasProfileChooser,
        bool HasCredentialInputs,
        bool HasGuestLoginButton,
        bool HasLoggedInProfile,
        bool HasPasswordValue,
        bool HasSubmitButton,
        string? Url);

    public sealed record RecoveryResult(
        bool Recovered,
        bool StillNeedsLogin,
        bool HasCaptcha,
        string? FailureReason,
        IReadOnlyList<string> Steps);

    public static ProbeState? TryParseProbe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return new ProbeState(
                NeedsLogin: root.TryGetProperty("needsLogin", out var nl) && nl.ValueKind == JsonValueKind.True,
                IsAuthorized: root.TryGetProperty("isAuthorized", out var auth) && auth.ValueKind == JsonValueKind.True,
                HasCaptcha: root.TryGetProperty("hasCaptcha", out var cap) && cap.ValueKind == JsonValueKind.True,
                HasLoginForm: root.TryGetProperty("hasLoginForm", out var lf) && lf.ValueKind == JsonValueKind.True,
                HasUsersList: root.TryGetProperty("hasUsersList", out var ul) && ul.ValueKind == JsonValueKind.True,
                HasSavedUserCard: root.TryGetProperty("hasSavedUserCard", out var su) && su.ValueKind == JsonValueKind.True,
                HasOtherProfileLink: root.TryGetProperty("hasOtherProfileLink", out var op) && op.ValueKind == JsonValueKind.True,
                HasProfileChooser: root.TryGetProperty("hasProfileChooser", out var pc) && pc.ValueKind == JsonValueKind.True,
                HasCredentialInputs: root.TryGetProperty("hasCredentialInputs", out var ci) && ci.ValueKind == JsonValueKind.True,
                HasGuestLoginButton: root.TryGetProperty("hasGuestLoginButton", out var gl) && gl.ValueKind == JsonValueKind.True,
                HasLoggedInProfile: root.TryGetProperty("hasLoggedInProfile", out var lp) && lp.ValueKind == JsonValueKind.True,
                HasPasswordValue: root.TryGetProperty("hasPasswordValue", out var pv) && pv.ValueKind == JsonValueKind.True,
                HasSubmitButton: root.TryGetProperty("hasSubmitButton", out var sb) && sb.ValueKind == JsonValueKind.True,
                Url: root.TryGetProperty("url", out var url) ? url.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<RecoveryResult> TryRecoverAsync(
        IPage page,
        CancellationToken cancellationToken = default) =>
        await TryRecoverAsync(page, credentials: null, cancellationToken).ConfigureAwait(false);

    public static async Task<RecoveryResult> TryRecoverAsync(
        IPage page,
        AvitoLoginCredentials? credentials,
        CancellationToken cancellationToken = default)
    {
        credentials ??= AvitoAutoLoginContext.Credentials;
        var steps = new List<string>();
        const int maxIterations = 10;

        var initialState = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
        if (initialState is { NeedsLogin: true })
        {
            if (await TryRefreshSessionAsync(page, steps, cancellationToken).ConfigureAwait(false))
            {
                steps.Add("сессия восстановлена");
                await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded after page refresh.", steps, page.Url)
                    .ConfigureAwait(false);
                return new RecoveryResult(true, false, false, null, steps);
            }
        }

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return new RecoveryResult(false, true, false, "probe_failed", steps);
            }

            if (state.HasCaptcha)
            {
                steps.Add("капча или блок IP");
                return new RecoveryResult(false, true, true, "captcha", steps);
            }

            if (state.IsAuthorized || !state.NeedsLogin)
            {
                steps.Add("сессия восстановлена");
                await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded.", steps, state.Url)
                    .ConfigureAwait(false);
                return new RecoveryResult(true, false, false, null, steps);
            }

            var progressed = false;
            var hasOrbitCredentials = credentials is { IsUsable: true };

            // 1) Есть сохранённый профиль («База») — кликаем его, затем вводим пароль.
            // «Войти в другой профиль» на этом экране не трогаем, пока есть карточка.
            if (!state.HasCredentialInputs &&
                (state.HasUsersList || state.HasSavedUserCard || state.HasProfileChooser))
            {
                var selected = await TrySelectSavedUserAsync(page, cancellationToken).ConfigureAwait(false);
                steps.Add(selected ? "выбран сохранённый профиль" : "не удалось выбрать профиль");
                if (selected)
                {
                    progressed = true;
                    await Task.Delay(MonitoringTiming.AutoLoginAfterUserSelectMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Карточки нет / клик не сработал — только тогда полный вход.
                if (state.HasOtherProfileLink)
                {
                    var switched = await TrySwitchToOtherProfileAsync(page, cancellationToken).ConfigureAwait(false);
                    steps.Add(switched
                        ? "открыт вход в другой профиль"
                        : "не удалось открыть вход в другой профиль");
                    if (switched)
                    {
                        progressed = true;
                        await Task.Delay(MonitoringTiming.AutoLoginAfterUserSelectMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
            }

            // 2) Поле пароля (после выбора профиля) или полная форма — credentials из Орбиты.
            if (state.HasCredentialInputs && hasOrbitCredentials)
            {
                var submitted = await TryFillCredentialsAndSubmitAsync(
                        page,
                        credentials!,
                        cancellationToken)
                    .ConfigureAwait(false);
                steps.Add(submitted
                    ? "введены логин/пароль из Орбиты"
                    : "не удалось ввести логин/пароль из Орбиты");
                if (submitted)
                {
                    progressed = true;
                    await WaitForAuthSettleAsync(page, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            // 3) Пароль уже в браузере — просто submit.
            if (state.HasLoginForm && state.HasPasswordValue && state.HasSubmitButton)
            {
                var submitted = await TrySubmitPasswordFormAsync(page, cancellationToken).ConfigureAwait(false);
                steps.Add(submitted ? "отправлена форма с сохранённым паролем" : "пароль не заполнен");
                if (submitted)
                {
                    progressed = true;
                    await WaitForAuthSettleAsync(page, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            // 4) Нет сохранённого профиля, только ссылка на полный вход.
            if (!state.HasCredentialInputs &&
                state.HasOtherProfileLink &&
                !state.HasUsersList &&
                !state.HasSavedUserCard)
            {
                var switched = await TrySwitchToOtherProfileAsync(page, cancellationToken).ConfigureAwait(false);
                steps.Add(switched
                    ? "открыт вход в другой профиль"
                    : "не удалось открыть вход в другой профиль");
                if (switched)
                {
                    progressed = true;
                    await Task.Delay(MonitoringTiming.AutoLoginAfterUserSelectMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            // 5) Autofill пароля в браузере (без credentials Орбиты).
            if (state.HasLoginForm && !state.HasPasswordValue && state.HasCredentialInputs)
            {
                await TryTriggerPasswordAutofillAsync(page, cancellationToken).ConfigureAwait(false);
                var afterAutofill = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
                if (afterAutofill is { HasPasswordValue: true, HasSubmitButton: true })
                {
                    var submitted = await TrySubmitPasswordFormAsync(page, cancellationToken).ConfigureAwait(false);
                    steps.Add(submitted ? "автозаполнение пароля и вход" : "автозаполнение не сработало");
                    if (submitted)
                    {
                        progressed = true;
                        await WaitForAuthSettleAsync(page, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                else if (!hasOrbitCredentials)
                {
                    steps.Add("пароль не сохранён в браузере и нет credentials в Орбите");
                }
            }

            if (state.HasGuestLoginButton ||
                (!state.HasLoginForm && !state.HasUsersList && !state.HasSavedUserCard && !state.HasOtherProfileLink))
            {
                var opened = await TryOpenLoginAsync(page, cancellationToken).ConfigureAwait(false);
                steps.Add(opened ? "открыта форма входа" : "кнопка входа не найдена");
                if (opened)
                {
                    progressed = true;
                    await Task.Delay(MonitoringTiming.AutoLoginAfterOpenLoginMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        var finalState = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
        if (finalState is { IsAuthorized: true } or { NeedsLogin: false, HasCaptcha: false })
        {
            steps.Add("сессия восстановлена");
            await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded after final probe.", steps, finalState.Url)
                .ConfigureAwait(false);
            return new RecoveryResult(true, false, false, null, steps);
        }

        var reason = finalState switch
        {
            { HasCaptcha: true } => "captcha",
            { HasLoginForm: true, HasPasswordValue: false } when credentials is not { IsUsable: true }
                => "no_saved_password",
            { HasLoginForm: true } when credentials is { IsUsable: true }
                => "credentials_login_failed",
            _ => "login_ui_stuck"
        };

        await LogAsync(
                DeskLinkAuditLogLevel.Warning,
                $"Avito auto-login failed ({reason}).",
                steps,
                finalState?.Url)
            .ConfigureAwait(false);

        return new RecoveryResult(
            false,
            finalState?.NeedsLogin ?? true,
            finalState?.HasCaptcha ?? false,
            reason,
            steps);
    }

    private static async Task<ProbeState?> ProbeAsync(IPage page, CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryParseProbe(raw);
    }

    /// <summary>
    /// Устаревшая вкладка Avito может показывать «гостя» до обновления — сначала reload, затем чистый dashboard.
    /// </summary>
    public static Task<bool> TryRefreshSessionAsync(
        IPage page,
        CancellationToken cancellationToken = default) =>
        TryRefreshSessionAsync(page, steps: null, cancellationToken);

    private static async Task<bool> TryRefreshSessionAsync(
        IPage page,
        List<string>? steps,
        CancellationToken cancellationToken)
    {
        steps?.Add("обновление вкладки");
        await TryReloadPageAsync(page, cancellationToken).ConfigureAwait(false);
        await Task.Delay(MonitoringTiming.AutoLoginPreRefreshSettleMs, cancellationToken).ConfigureAwait(false);

        var afterReload = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
        if (IsSessionRecovered(afterReload))
        {
            steps?.Add("сессия активна после обновления");
            return true;
        }

        steps?.Add("переход на dashboard");
        await TryNavigateToDashboardAsync(page, cancellationToken).ConfigureAwait(false);
        await Task.Delay(MonitoringTiming.AutoLoginDashboardNavSettleMs, cancellationToken).ConfigureAwait(false);

        var afterDashboard = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
        if (IsSessionRecovered(afterDashboard))
        {
            steps?.Add("сессия активна после перехода на dashboard");
            return true;
        }

        return false;
    }

    public static bool IsSessionRecovered(ProbeState? state) =>
        state is { IsAuthorized: true } or { NeedsLogin: false, HasCaptcha: false };

    private static async Task TryReloadPageAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.ReloadAsync(45_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            await page.ReloadAsync(45_000).ConfigureAwait(false);
        }
    }

    private static async Task TryNavigateToDashboardAsync(IPage page, CancellationToken cancellationToken)
    {
        var options = new NavigationOptions
        {
            Timeout = 45_000,
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
        };

        try
        {
            await page.GoToAsync(ProfileDashboardPageUrl, options).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            await page.GoToAsync(ProfileDashboardPageUrl, options).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryOpenLoginAsync(IPage page, CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildOpenLoginScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "clicked");
    }

    private static async Task<bool> TrySelectSavedUserAsync(IPage page, CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildSelectSavedUserScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "clicked");
    }

    private static async Task<bool> TrySwitchToOtherProfileAsync(IPage page, CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(
                page,
                AvitoAutoLoginScripts.BuildSwitchToOtherProfileScript(),
                cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "clicked");
    }

    private static async Task<bool> TrySubmitPasswordFormAsync(IPage page, CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildSubmitPasswordFormScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "submitted");
    }

    private static async Task<bool> TryFillCredentialsAndSubmitAsync(
        IPage page,
        AvitoLoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        var raw = await EvaluateJsonStringAsync(
                page,
                AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript(credentials.Login, credentials.Password),
                cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "submitted");
    }

    private static async Task TryTriggerPasswordAutofillAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var i = 0; i < MonitoringTiming.AutoLoginPasswordAutofillPolls; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildSubmitPasswordFormScript(), cancellationToken)
                .ConfigureAwait(false);
            var state = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
            if (state is { HasPasswordValue: true })
            {
                return;
            }

            await Task.Delay(MonitoringTiming.AutoLoginPasswordAutofillPollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForAuthSettleAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var elapsed = 0; elapsed < MonitoringTiming.AutoLoginPostSubmitMaxWaitMs;
             elapsed += MonitoringTiming.AutoLoginPostSubmitPollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
            if (state is { IsAuthorized: true } or { NeedsLogin: false, HasCaptcha: false })
            {
                return;
            }

            if (state is { HasCaptcha: true })
            {
                return;
            }

            await Task.Delay(MonitoringTiming.AutoLoginPostSubmitPollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task LogAsync(
        DeskLinkAuditLogLevel level,
        string message,
        IReadOnlyList<string> steps,
        string? url) =>
        GlobalLogger.Instance.LogAsync(
            message,
            level,
            memberName: nameof(AvitoAutoLoginRecovery),
            properties: new Dictionary<string, object?>
            {
                ["autoLogin.steps"] = string.Join(" → ", steps),
                ["autoLogin.url"] = url
            });

    private static async Task<string?> EvaluateJsonStringAsync(
        IPage page,
        string script,
        CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<string>($"JSON.stringify({script})").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            return await page.EvaluateExpressionAsync<string>($"JSON.stringify({script})").ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadBoolProperty(string? raw, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            return doc.RootElement.TryGetProperty(propertyName, out var prop)
                   && prop.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRecoverableNavigationError(Exception ex) =>
        ex is PuppeteerException &&
        (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("frame got detached", StringComparison.OrdinalIgnoreCase));

    private static string UnwrapJsonString(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(t) ?? t;
            }
            catch
            {
                return t;
            }
        }

        return t;
    }
}