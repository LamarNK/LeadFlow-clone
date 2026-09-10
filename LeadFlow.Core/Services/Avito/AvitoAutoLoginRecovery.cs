using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Пытается восстановить сессию Avito через UI входа.
/// Единственный источник логина и пароля — Орбита.
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
        string? Url,
        bool RequiresPasswordResetSms = false,
        string? PasswordResetSmsPhone = null);

    public sealed record RecoveryResult(
        bool Recovered,
        bool StillNeedsLogin,
        bool HasCaptcha,
        string? FailureReason,
        IReadOnlyList<string> Steps,
        string? PasswordResetSmsPhone = null);

    public enum LoginCaptchaDecision
    {
        Ignore,
        Solve,
        Abort
    }

    public const int MaxLoginCaptchaAttempts = 3;

    internal const int MaxProbeAttempts = 4;
    internal const int ProbeRetryDelayMs = 600;

    public static LoginCaptchaDecision DecideCaptcha(
        ProbeState state,
        bool solverAvailable,
        int attempts,
        int maxAttempts)
    {
        if (!state.HasCaptcha)
        {
            return LoginCaptchaDecision.Ignore;
        }

        if (solverAvailable && attempts < maxAttempts)
        {
            return LoginCaptchaDecision.Solve;
        }

        return LoginCaptchaDecision.Abort;
    }

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
                Url: root.TryGetProperty("url", out var url) ? url.GetString() : null,
                RequiresPasswordResetSms: root.TryGetProperty("requiresPasswordResetSms", out var passwordResetSms)
                    && passwordResetSms.ValueKind == JsonValueKind.True,
                PasswordResetSmsPhone: root.TryGetProperty("passwordResetSmsPhone", out var passwordResetPhone)
                    ? passwordResetPhone.GetString()
                    : null);
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
        CancellationToken cancellationToken = default) =>
        await TryRecoverAsync(page, credentials, trySolveCaptcha: null, cancellationToken).ConfigureAwait(false);

    public static async Task<RecoveryResult> TryRecoverAsync(
        IPage page,
        AvitoLoginCredentials? credentials,
        Func<IPage, CancellationToken, Task<bool>>? trySolveCaptcha,
        CancellationToken cancellationToken = default)
    {
        credentials ??= AvitoAutoLoginContext.Credentials;
        trySolveCaptcha ??= AvitoAutoLoginContext.CaptchaSolver;
        var steps = new List<string>();
        const int maxIterations = 10;
        var captchaAttempts = 0;

        if (credentials is not { IsUsable: true })
        {
            steps.Add("credentials Орбиты: нет — автовход невозможен");
            await LogAsync(
                    DeskLinkAuditLogLevel.Warning,
                    "Avito auto-login aborted: no Orbit credentials.",
                    steps,
                    page.Url)
                .ConfigureAwait(false);
            return Track(new RecoveryResult(false, true, false, "no_orbit_credentials", steps));
        }

        steps.Add("credentials Орбиты: есть");

        var initialState = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
        if (initialState?.RequiresPasswordResetSms == true)
        {
            return await StopForPasswordResetSmsAsync(initialState, steps).ConfigureAwait(false);
        }

        var hasSavedUserCard = (initialState is { HasUsersList: true }
            or { HasSavedUserCard: true })
            || await HasSavedUserCardInDomAsync(page, cancellationToken).ConfigureAwait(false);
        if (initialState is not null && ShouldRefreshSession(initialState, hasSavedUserCard))
        {
            if (await TryRefreshSessionAsync(page, steps, cancellationToken).ConfigureAwait(false))
            {
                steps.Add("сессия восстановлена");
                await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded after page refresh.", steps, page.Url)
                    .ConfigureAwait(false);
                return Track(new RecoveryResult(true, false, false, null, steps));
            }
        }

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ProbeAsync(page, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                // После Reload GeeTest CDP-проба может не успеть в живой execution context.
                // Селекторы Puppeteer независимы от результата JSON-probe: не сносим users-list refresh-ом.
                if (await HasSavedUserCardInDomAsync(page, cancellationToken).ConfigureAwait(false))
                {
                    var selected = await TrySelectSavedUserAsync(page, credentials.Login, cancellationToken)
                        .ConfigureAwait(false);
                    steps.Add(selected
                        ? "выбран сохранённый профиль (fallback без probe)"
                        : "найден сохранённый профиль, но клик не сработал");
                    if (selected)
                    {
                        await Task.Delay(MonitoringTiming.AutoLoginAfterUserSelectMs, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                steps.Add("probe не прочитался");
                await LogAsync(
                        DeskLinkAuditLogLevel.Warning,
                        "Avito auto-login failed (probe_failed).",
                        steps,
                        page.Url)
                    .ConfigureAwait(false);
                return Track(new RecoveryResult(false, true, false, "probe_failed", steps));
            }

            if (state.RequiresPasswordResetSms)
            {
                return await StopForPasswordResetSmsAsync(state, steps).ConfigureAwait(false);
            }

            var captchaDecision = DecideCaptcha(
                state,
                trySolveCaptcha is not null,
                captchaAttempts,
                MaxLoginCaptchaAttempts);
            if (captchaDecision == LoginCaptchaDecision.Abort)
            {
                steps.Add("капча или блок IP");
                await LogAsync(
                        DeskLinkAuditLogLevel.Warning,
                        "Avito auto-login failed (captcha).",
                        steps,
                        state.Url)
                    .ConfigureAwait(false);
                return Track(new RecoveryResult(false, true, true, "captcha", steps));
            }

            if (captchaDecision == LoginCaptchaDecision.Solve)
            {
                captchaAttempts++;
                var solved = false;
                try
                {
                    solved = await trySolveCaptcha!(page, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    steps.Add($"капча логина ошибка: {ex.Message}");
                }

                steps.Add(solved ? "капча логина пройдена" : "капча логина не пройдена");
                if (solved)
                {
                    await WaitForAuthSettleAsync(page, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await LogAsync(
                        DeskLinkAuditLogLevel.Warning,
                        "Avito auto-login failed (captcha).",
                        steps,
                        state.Url)
                    .ConfigureAwait(false);
                return Track(new RecoveryResult(false, true, true, "captcha", steps));
            }

            if (state.IsAuthorized || !state.NeedsLogin)
            {
                steps.Add("сессия восстановлена");
                await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded.", steps, state.Url)
                    .ConfigureAwait(false);
                return Track(new RecoveryResult(true, false, false, null, steps));
            }

            var progressed = false;
            // 1) Есть сохранённый профиль («База») — кликаем его, затем вводим пароль.
            // «Войти в другой профиль» на этом экране не трогаем, пока есть карточка.
            if (!state.HasCredentialInputs &&
                (state.HasUsersList || state.HasSavedUserCard || state.HasProfileChooser))
            {
                var selected = await TrySelectSavedUserAsync(page, credentials.Login, cancellationToken)
                    .ConfigureAwait(false);
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
            if (state.HasCredentialInputs)
            {
                var submitted = await TryFillCredentialsAndSubmitAsync(
                        page,
                        credentials,
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

            // 3) Нет сохранённого профиля, только ссылка на полный вход.
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
        if (finalState?.RequiresPasswordResetSms == true)
        {
            return await StopForPasswordResetSmsAsync(finalState, steps).ConfigureAwait(false);
        }

        if (finalState is { IsAuthorized: true } or { NeedsLogin: false, HasCaptcha: false })
        {
            steps.Add("сессия восстановлена");
            await LogAsync(DeskLinkAuditLogLevel.Info, "Avito auto-login succeeded after final probe.", steps, finalState.Url)
                .ConfigureAwait(false);
            return Track(new RecoveryResult(true, false, false, null, steps));
        }

        var reason = finalState switch
        {
            { HasCaptcha: true } => "captcha",
            { HasLoginForm: true }
                => "credentials_login_failed",
            _ => "login_ui_stuck"
        };

        await LogAsync(
                DeskLinkAuditLogLevel.Warning,
                $"Avito auto-login failed ({reason}).",
                steps,
                finalState?.Url)
            .ConfigureAwait(false);

        return Track(new RecoveryResult(
            false,
            finalState?.NeedsLogin ?? true,
            finalState?.HasCaptcha ?? false,
            reason,
            steps));
    }

    private static async Task<RecoveryResult> StopForPasswordResetSmsAsync(
        ProbeState state,
        List<string> steps)
    {
        var phone = string.IsNullOrWhiteSpace(state.PasswordResetSmsPhone)
            ? string.Empty
            : $" ({state.PasswordResetSmsPhone})";
        steps.Add($"Avito сбросил пароль: требуется SMS-код{phone}");
        await LogAsync(
                DeskLinkAuditLogLevel.Warning,
                "Avito auto-login stopped (password_reset_sms_required).",
                steps,
                state.Url)
            .ConfigureAwait(false);
        return Track(new RecoveryResult(false, true, false, "password_reset_sms_required", steps, state.PasswordResetSmsPhone));
    }

    private static RecoveryResult Track(RecoveryResult result)
    {
        AvitoAutoLoginContext.RecordAttemptResult(result.Recovered);
        return result;
    }

    private static async Task<ProbeState?> ProbeAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await EvaluateJsonStringAsync(page, AvitoAutoLoginScripts.BuildProbeScript(), cancellationToken)
                .ConfigureAwait(false);
            var state = TryParseProbe(raw);
            if (state is not null)
            {
                return state;
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"Avito auto-login probe returned no state (attempt {attempt}/{MaxProbeAttempts}).",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(AvitoAutoLoginRecovery),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "auto_login_probe_retry",
                    ["autoLogin.probeAttempt"] = attempt,
                    ["autoLogin.probeMaxAttempts"] = MaxProbeAttempts,
                    ["autoLogin.url"] = page.Url
                });

            if (attempt < MaxProbeAttempts)
            {
                await Task.Delay(ProbeRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
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

    private static readonly string[] OtherProfileClickSelectors =
    [
        "[data-marker='users-list/button']",
        "[data-marker='login-form/other']",
        "[data-marker='login-form/other-profile']"
    ];

    private static readonly string[] PasswordInputSelectors =
    [
        "[data-marker='login-form/password/input']",
        "form[data-marker='login-form'] input[name='password']",
        "input[name='password'][autocomplete='current-password']",
        "input[type='password']"
    ];

    private static readonly string[] LoginInputSelectors =
    [
        "[data-marker='login-form/login/input']",
        "[data-marker='login-form/login'] input",
        "input[name='login'][autocomplete='username']",
        "input[name='login']",
        "input[autocomplete='username']"
    ];

    private static async Task<bool> TrySelectSavedUserAsync(
        IPage page,
        string? preferredLogin,
        CancellationToken cancellationToken)
    {
        if (await TryClickAndWaitForCredentialsAsync(
                page,
                () => TryClickMatchingSavedUserAsync(page, preferredLogin, useHumanPointer: true, cancellationToken),
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryClickAndWaitForCredentialsAsync(
                page,
                () => TryClickMatchingSavedUserAsync(page, preferredLogin, useHumanPointer: false, cancellationToken),
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var raw = await EvaluateJsonStringAsync(
                page,
                AvitoAutoLoginScripts.BuildSelectSavedUserScript(preferredLogin),
                cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "clicked")
               && await WaitForCredentialInputsAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryClickMatchingSavedUserAsync(
        IPage page,
        string? preferredLogin,
        bool useHumanPointer,
        CancellationToken cancellationToken)
    {
        IElementHandle[] handles;
        try
        {
            handles = await page.QuerySelectorAllAsync(
                    "[data-marker='users-list'] [data-marker='user/link'], [data-marker^='users-list('] [data-marker='user/link'], button[data-marker='user/link']")
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (handles.Length == 0)
        {
            return false;
        }

        var want = NormalizePhoneDigits(preferredLogin);
        IElementHandle? first = null;
        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? label;
            try
            {
                label = await handle.EvaluateFunctionAsync<string>(
                        "el => ((el.getAttribute('aria-label') || '') + ' ' + (el.textContent || '')).trim()")
                    .ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            first ??= handle;
            var got = NormalizePhoneDigits(label);
            if (string.IsNullOrEmpty(want) || PhonesMatch(want, got))
            {
                if (await TryClickHandleAsync(page, handle, useHumanPointer, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        // Телефон из Орбиты не совпал ни с одной карточкой — не жмём чужой профиль.
        if (!string.IsNullOrEmpty(want))
        {
            return false;
        }

        return first is not null
               && await TryClickHandleAsync(page, first, useHumanPointer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryClickHandleAsync(
        IPage page,
        IElementHandle handle,
        bool useHumanPointer,
        CancellationToken cancellationToken)
    {
        try
        {
            if (useHumanPointer)
            {
                return await AvitoHumanPointer.TryClickHandleAsync(page, handle, cancellationToken)
                    .ConfigureAwait(false);
            }

            await handle.EvaluateFunctionAsync("el => el.scrollIntoView({ block: 'center', inline: 'center' })")
                .ConfigureAwait(false);
            await handle.ClickAsync(new ClickOptions { Delay = 35 }).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePhoneDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new PhoneNormalizer().Normalize(value);
    }

    private static bool PhonesMatch(string want, string got) =>
        !string.IsNullOrEmpty(want) &&
        !string.IsNullOrEmpty(got) &&
        (want == got ||
         want.EndsWith(got, StringComparison.Ordinal) ||
         got.EndsWith(want, StringComparison.Ordinal));

    private static async Task<bool> TrySwitchToOtherProfileAsync(IPage page, CancellationToken cancellationToken)
    {
        if (await TryClickAndWaitForCredentialsAsync(
                page,
                () => TryMouseClickFirstAsync(page, OtherProfileClickSelectors, cancellationToken),
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryClickAndWaitForCredentialsAsync(
                page,
                () => TryClickFirstAsync(page, OtherProfileClickSelectors, cancellationToken),
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var raw = await EvaluateJsonStringAsync(
                page,
                AvitoAutoLoginScripts.BuildSwitchToOtherProfileScript(),
                cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "clicked")
               && await WaitForCredentialInputsAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryFillCredentialsAndSubmitAsync(
        IPage page,
        AvitoLoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!await WaitForCredentialInputsAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // CDP-ввод очищает сохранённый браузером пароль, затем JS дублирует
        // значение через React value tracker и отправляет именно форму Avito.
        _ = await TryTypeOrbitCredentialsAsync(page, credentials, cancellationToken).ConfigureAwait(false);
        var raw = await EvaluateJsonStringAsync(
                page,
                AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript(credentials.Login, credentials.Password),
                cancellationToken)
            .ConfigureAwait(false);
        return TryReadBoolProperty(raw, "submitted");
    }

    private static async Task<bool> TryClickAndWaitForCredentialsAsync(
        IPage page,
        Func<Task<bool>> click,
        CancellationToken cancellationToken)
    {
        if (!await click().ConfigureAwait(false))
        {
            return false;
        }

        return await WaitForCredentialInputsAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForCredentialInputsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='login-form/password/input'], form[data-marker='login-form'] input[name='password'], input[type='password']",
                    new WaitForSelectorOptions { Timeout = 6_000, Visible = true })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryTypeOrbitCredentialsAsync(
        IPage page,
        AvitoLoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            var password = await QueryFirstAsync(page, PasswordInputSelectors).ConfigureAwait(false);
            if (password is null)
            {
                return false;
            }

            var login = await QueryFirstAsync(page, LoginInputSelectors).ConfigureAwait(false);
            if (login is not null)
            {
                var locked = await login.EvaluateFunctionAsync<bool>(
                        "el => !!(el.readOnly || el.disabled || el.type === 'hidden' || getComputedStyle(el).display === 'none')")
                    .ConfigureAwait(false);
                if (!locked)
                {
                    await ClearAndTypeAsync(page, login, credentials.Login, cancellationToken).ConfigureAwait(false);
                }
            }

            await ClearAndTypeAsync(page, password, credentials.Password, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ClearAndTypeAsync(
        IPage page,
        IElementHandle input,
        string value,
        CancellationToken cancellationToken)
    {
        await input.ClickAsync(new ClickOptions { Delay = Random.Shared.Next(35, 80) }).ConfigureAwait(false);
        await Task.Delay(Random.Shared.Next(60, 140), cancellationToken).ConfigureAwait(false);
        await page.Keyboard.DownAsync("Control").ConfigureAwait(false);
        await page.Keyboard.PressAsync("KeyA").ConfigureAwait(false);
        await page.Keyboard.UpAsync("Control").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Backspace").ConfigureAwait(false);
        await input.TypeAsync(value, new TypeOptions { Delay = HumanDelay.NextTypeCharDelayMs() }).ConfigureAwait(false);
    }

    private static async Task<bool> TryMouseClickFirstAsync(
        IPage page,
        IReadOnlyList<string> selectors,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handle = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                if (handle is null)
                {
                    continue;
                }

                if (await AvitoHumanPointer.TryClickHandleAsync(page, handle, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
                // Try the next selector.
            }
        }

        return false;
    }

    private static async Task<bool> TryClickFirstAsync(
        IPage page,
        IReadOnlyList<string> selectors,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handle = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                if (handle is null)
                {
                    continue;
                }

                await handle.EvaluateFunctionAsync("el => el.scrollIntoView({ block: 'center', inline: 'center' })")
                    .ConfigureAwait(false);
                await handle.ClickAsync(new ClickOptions { Delay = 35 }).ConfigureAwait(false);
                return true;
            }
            catch
            {
                // Try the next selector.
            }
        }

        return false;
    }

    private static async Task<IElementHandle?> QueryFirstAsync(IPage page, IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var handle = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                if (handle is not null)
                {
                    return handle;
                }
            }
            catch
            {
                // Try the next selector.
            }
        }

        return null;
    }

    internal static bool HasVisibleLoginUi(ProbeState state) =>
        state.HasLoginForm ||
        state.HasUsersList ||
        state.HasSavedUserCard ||
        state.HasOtherProfileLink ||
        state.HasCredentialInputs;

    /// <summary>
    /// Полный reload допустим только для действительно пустого гостевого состояния.
    /// Показанная капча уже является UI входа: reload в этот момент сбрасывает
    /// первый ввод пароля и запускает второй вход.
    /// </summary>
    internal static bool ShouldRefreshSession(ProbeState state, bool hasSavedUserCard) =>
        state.NeedsLogin &&
        !state.HasCaptcha &&
        !HasVisibleLoginUi(state) &&
        !hasSavedUserCard;

    private static async Task<bool> HasSavedUserCardInDomAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await page.QuerySelectorAsync("[data-marker='users-list']") is not null
                || await page.QuerySelectorAsync("[data-marker='users-list'] [data-marker='user/link'], [data-marker^='users-list('] [data-marker='user/link'], button[data-marker='user/link']") is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Avito auto-login saved-profile selector failed: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(AvitoAutoLoginRecovery),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "auto_login_saved_profile_probe_failed",
                    ["autoLogin.url"] = page.Url
                });
            return false;
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
            return await page.EvaluateExpressionAsync<string>(BuildJsonEvaluationExpression(script)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Avito auto-login probe evaluation failed: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(AvitoAutoLoginRecovery),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "auto_login_probe_evaluate_failed",
                    ["autoLogin.evaluateError"] = ex.Message,
                    ["autoLogin.url"] = page.Url,
                    ["autoLogin.recoverableNavigationError"] = IsRecoverableNavigationError(ex)
                });
            return null;
        }
    }

    internal static string BuildJsonEvaluationExpression(string script)
    {
        var expression = script.Trim();
        if (expression.EndsWith(';'))
        {
            expression = expression[..^1];
        }

        return $"JSON.stringify({expression})";
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
