using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CrmTelephonyService(
    OrbitaDbContext db,
    PhoneNormalizer phoneNormalizer,
    TimeProvider timeProvider,
    IPanelRealtimeNotifier? panelRealtime = null,
    CrmTelephonyCredentialProtector? credentialProtector = null,
    CrmCallRecordingStorageService? callRecordingStorage = null,
    CrmSipRuntimeConfigWriter? sipRuntimeConfigWriter = null)
{
    private static readonly TimeSpan CallbackAffinityLifetime = TimeSpan.FromDays(30);

    public async Task<CrmTelephonySettingsDto?> GetSettingsAsync(
        Guid officeId,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        provider = NormalizeProvider(provider);
        if (!await db.Offices.AsNoTracking().AnyAsync(x => x.Id == officeId, ct))
        {
            return null;
        }

        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == provider, ct);
        var bindings = await (
            from binding in db.CrmTelephonyUserBindings.AsNoTracking()
            join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
            join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
            where binding.OfficeId == officeId && binding.Provider == provider
            orderby profile.FullName, user.Email
            select new CrmTelephonyUserBindingDto(
                binding.UserId,
                string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                binding.ProviderUserKey,
                binding.OutboundProvider))
            .ToListAsync(ct);

        CrmSipProviderAccountDto? sipAccount = null;
        if (provider == CrmTelephonyProviders.Beeline
            && receiver is not null
            && credentialProtector is not null
            && !string.IsNullOrWhiteSpace(receiver.ProviderAccessTokenProtected))
        {
            try
            {
                var protectedPayload = credentialProtector.Unprotect(receiver.ProviderAccessTokenProtected);
                var payload = JsonSerializer.Deserialize<SipProviderCredentialPayload>(protectedPayload);
                if (payload is not null)
                {
                    var runtimeStatus = sipRuntimeConfigWriter is null
                        ? new CrmSipRuntimeStatus("runtime-unavailable", null)
                        : await sipRuntimeConfigWriter.ReadBeelineStatusAsync(officeId, ct);
                    sipAccount = new CrmSipProviderAccountDto(
                        payload.Server,
                        payload.Domain,
                        payload.Port,
                        payload.Transport,
                        payload.SipLogin,
                        payload.AuthorizationLogin,
                        !string.IsNullOrWhiteSpace(payload.Password),
                        payload.UseForOutbound,
                        runtimeStatus.Status,
                        runtimeStatus.CheckedAtUtc);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                sipAccount = null;
            }
        }

        var credentialsConfigured = provider switch
        {
            CrmTelephonyProviders.Plusofon => !string.IsNullOrWhiteSpace(receiver?.ProviderClientId)
                && !string.IsNullOrWhiteSpace(receiver?.ProviderAccessTokenProtected),
            CrmTelephonyProviders.Beeline => sipAccount is not null,
            _ => false
        };

        return new CrmTelephonySettingsDto(
            officeId,
            provider,
            receiver is not null,
            receiver?.IsEnabled == true,
            receiver?.PublicId,
            bindings,
            credentialsConfigured,
            sipAccount);
    }

    public async Task<(bool Success, string? Error)> SetPlusofonCredentialsAsync(
        Guid officeId,
        string clientId,
        string accessToken,
        CancellationToken ct = default)
    {
        clientId = clientId.Trim();
        accessToken = accessToken.Trim();
        if (clientId.Length is 0 or > 128 || accessToken.Length is 0 or > 4096)
        {
            return (false, "Укажите корректные Client ID и токен Плюсофона.");
        }
        if (credentialProtector is null)
        {
            return (false, "Защита реквизитов телефонии недоступна.");
        }

        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Plusofon, ct);
        if (receiver is null)
        {
            return (false, "Сначала создайте приёмник webhook Плюсофона.");
        }

        receiver.ProviderClientId = clientId;
        receiver.ProviderAccessTokenProtected = credentialProtector.Protect(accessToken);
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> SetBeelineSipAccountAsync(
        Guid officeId,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct = default)
    {
        if (credentialProtector is null)
        {
            return (false, "Защита реквизитов телефонии недоступна.");
        }
        if (sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Общий runtime-каталог API и SIP-сервера не настроен.");
        }
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (false, "Офис не найден.");
        }

        var server = request.Server.Trim().ToLowerInvariant();
        var domain = string.IsNullOrWhiteSpace(request.Domain)
            ? server
            : request.Domain.Trim().ToLowerInvariant();
        var transport = request.Transport.Trim().ToLowerInvariant();
        var sipLogin = request.SipLogin.Trim();
        var authorizationLogin = request.AuthorizationLogin.Trim();
        if (!IsValidSipHost(server) || !IsValidSipHost(domain))
        {
            return (false, "Укажите корректные SIP proxy и домен без протокола sip://.");
        }
        if (request.Port is <= 0 or > 65535 || transport is not ("udp" or "tcp"))
        {
            return (false, "Порт должен быть от 1 до 65535, транспорт — UDP или TCP.");
        }
        if (sipLogin.Length is 0 or > 128 || authorizationLogin.Length is 0 or > 256
            || ContainsControlCharacters(sipLogin) || ContainsControlCharacters(authorizationLogin))
        {
            return (false, "Укажите корректные SIP User ID и Authorization User ID.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Beeline, ct);
        SipProviderCredentialPayload? previous = null;
        if (receiver is not null && !string.IsNullOrWhiteSpace(receiver.ProviderAccessTokenProtected))
        {
            try
            {
                previous = JsonSerializer.Deserialize<SipProviderCredentialPayload>(
                    credentialProtector.Unprotect(receiver.ProviderAccessTokenProtected));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (false, "Сохранённые реквизиты Билайна повреждены. Укажите пароль заново.");
            }
        }

        var password = string.IsNullOrEmpty(request.Password) ? previous?.Password ?? string.Empty : request.Password;
        if (password.Length is 0 or > 4096 || ContainsControlCharacters(password))
        {
            return (false, "Укажите корректный SIP-пароль. При первой настройке он обязателен.");
        }

        var payload = new SipProviderCredentialPayload(
            server,
            domain,
            request.Port,
            transport,
            sipLogin,
            authorizationLogin,
            password,
            request.UseForOutbound);
        if (receiver is null)
        {
            receiver = new CrmTelephonyWebhookEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = CrmTelephonyProviders.Beeline,
                PublicId = Guid.NewGuid(),
                SecretHash = HashSecret(ToBase64Url(RandomNumberGenerator.GetBytes(32))),
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }

        receiver.ProviderClientId = sipLogin;
        receiver.ProviderAccessTokenProtected = credentialProtector.Protect(JsonSerializer.Serialize(payload));
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        var (applied, applyError) = await sipRuntimeConfigWriter.WriteBeelineAsync(
            officeId,
            new CrmSipRuntimeAccount(
                payload.Server,
                payload.Domain,
                payload.Port,
                payload.Transport,
                payload.SipLogin,
                payload.AuthorizationLogin,
                payload.Password,
                payload.UseForOutbound),
            ct);
        return applied
            ? (true, null)
            : (false, $"Реквизиты сохранены, но SIP-сервер не применил конфигурацию: {applyError}");
    }

    public async Task<(CrmTelephonyReceiverDto? Receiver, string? Error)> RotateReceiverAsync(
        Guid officeId,
        string publicBaseUrl,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        provider = NormalizeProvider(provider);
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (null, "Офис не найден.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var publicId = Guid.NewGuid();
        var secret = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == provider, ct);
        if (receiver is null)
        {
            receiver = new CrmTelephonyWebhookEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = provider,
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }

        receiver.PublicId = publicId;
        receiver.SecretHash = HashSecret(secret);
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        if (provider == CrmTelephonyProviders.Asterisk
            && sipRuntimeConfigWriter is not null
            && sipRuntimeConfigWriter.IsAvailable)
        {
            var (written, writeError) = await sipRuntimeConfigWriter.WriteAsteriskReceiverAsync(
                officeId,
                new CrmAsteriskRuntimeReceiver(publicId, secret),
                ct);
            if (!written)
            {
                return (null, $"Приёмник создан, но SIP-сервер не получил его настройки: {writeError}");
            }
        }

        var callbackUrl = $"{publicBaseUrl.TrimEnd('/')}/api/v1/integrations/telephony/{provider}/{publicId:D}";
        if (provider is CrmTelephonyProviders.Plusofon or CrmTelephonyProviders.Asterisk)
        {
            return (new CrmTelephonyReceiverDto(
                officeId,
                provider,
                publicId,
                callbackUrl,
                callbackUrl,
                secret,
                "X-Orbita-Webhook-Secret"), null);
        }

        // SIPOUT replaces %VAR:...% placeholders inside a WebRequest URL.
        // Keep these placeholders unescaped; escaping '%' would stop provider-side substitution.
        var sipoutUrl = $"{callbackUrl}?secret={Uri.EscapeDataString(secret)}"
            + "&CID=%VAR:CID%&DID=%VAR:DID%&C_ID=%VAR:C_ID%&C_TYPE=%VAR:C_TYPE%"
            + "&C_START=%VAR:C_START%&C_TIME=%VAR:C_TIME%&PREV_EXTEN=%VAR:PREV_EXTEN%"
            + "&LAST_CALLER=%VAR:LAST_CALLER%&LAST_RECORDING_URL=%VAR:LAST_RECORDING_URL%";
        return (new CrmTelephonyReceiverDto(officeId, provider, publicId, callbackUrl, sipoutUrl), null);
    }

    public async Task<bool> SetEnabledAsync(
        Guid officeId,
        bool enabled,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        provider = NormalizeProvider(provider);
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == provider, ct);
        if (receiver is null)
        {
            return false;
        }

        receiver.IsEnabled = enabled;
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(CrmTelephonyUserBindingDto? Binding, string? Error)> SetBindingAsync(
        Guid officeId,
        string userId,
        string providerUserKey,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout,
        string? outboundProvider = null)
    {
        provider = NormalizeProvider(provider);
        var normalizedKey = NormalizeProviderUserKey(providerUserKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return (null, "Укажите внутренний номер или логин провайдера.");
        }
        if (provider == CrmTelephonyProviders.Asterisk
            && (!normalizedKey.All(char.IsDigit) || normalizedKey.Length > 8))
        {
            return (null, "Внутренний номер должен содержать от 1 до 8 цифр.");
        }
        if (!CrmTelephonyOutboundProviders.IsSupported(outboundProvider))
        {
            return (null, "Выбрана неизвестная линия для исходящих звонков.");
        }

        var user = await (
            from profile in db.PanelUserProfiles
            join identity in db.Users on profile.UserId equals identity.Id
            where profile.UserId == userId && profile.OfficeId == officeId
            select new { Profile = profile, identity.Email })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return (null, "Пользователь этого офиса не найден.");
        }

        // A shared Asterisk serves every office. Its endpoint/extension namespace is
        // therefore global even though CRM bindings and calls remain office-scoped.
        var duplicate = await db.CrmTelephonyUserBindings.AsNoTracking()
            .AnyAsync(x => x.Provider == provider
                && x.ProviderUserKey == normalizedKey
                && x.UserId != userId
                && (provider == CrmTelephonyProviders.Asterisk || x.OfficeId == officeId), ct);
        if (duplicate)
        {
            return (null, provider == CrmTelephonyProviders.Asterisk
                ? "Этот внутренний номер уже используется сотрудником другого офиса. Для общего SIP-сервера номера должны быть уникальны."
                : "Этот аккаунт телефонии уже привязан к другому сотруднику.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var binding = await db.CrmTelephonyUserBindings
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == provider && x.UserId == userId, ct);
        if (binding is null)
        {
            binding = new CrmTelephonyUserBindingEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = provider,
                UserId = userId,
                CreatedAtUtc = now
            };
            db.CrmTelephonyUserBindings.Add(binding);
        }

        binding.ProviderUserKey = normalizedKey;
        binding.OutboundProvider = provider == CrmTelephonyProviders.Asterisk
            ? CrmTelephonyOutboundProviders.Normalize(outboundProvider)
            : CrmTelephonyOutboundProviders.Default;
        binding.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        if (provider == CrmTelephonyProviders.Asterisk)
        {
            await PublishUserOutboundRoutesAsync(officeId, ct);
        }
        var userName = string.IsNullOrWhiteSpace(user.Profile.FullName)
            ? user.Email ?? userId
            : user.Profile.FullName;
        return (new CrmTelephonyUserBindingDto(userId, userName, normalizedKey, binding.OutboundProvider), null);
    }

    public async Task<bool> RemoveBindingAsync(
        Guid officeId,
        string userId,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        provider = NormalizeProvider(provider);
        var binding = await db.CrmTelephonyUserBindings
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == provider && x.UserId == userId, ct);
        if (binding is null)
        {
            return false;
        }

        db.CrmTelephonyUserBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        if (provider == CrmTelephonyProviders.Asterisk)
        {
            await PublishUserOutboundRoutesAsync(officeId, ct);
        }
        return true;
    }

    private async Task PublishUserOutboundRoutesAsync(Guid officeId, CancellationToken ct)
    {
        if (sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return;
        }

        var routes = await db.CrmTelephonyUserBindings
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Asterisk)
            .ToDictionaryAsync(x => x.ProviderUserKey, x => x.OutboundProvider, ct);
        await sipRuntimeConfigWriter.WriteUserOutboundRoutesAsync(officeId, routes, ct);
    }

    public Task<SipoutCallReceiveResult> ReceiveSipoutCallAsync(
        Guid publicId,
        string? secret,
        SipoutCallWebhookPayload payload,
        CancellationToken ct = default) =>
        ReceiveCallAsync(CrmTelephonyProviders.Sipout, publicId, secret, payload, ct);

    public Task<SipoutCallReceiveResult> ReceivePlusofonCallAsync(
        Guid publicId,
        string? secret,
        PlusofonCallWebhookPayload payload,
        CancellationToken ct = default) =>
        ReceiveCallAsync(
            CrmTelephonyProviders.Plusofon,
            publicId,
            secret,
            new SipoutCallWebhookPayload(
                payload.ExternalCallId,
                payload.CallerPhone,
                payload.CalledPhone,
                payload.Direction,
                payload.InternalNumber,
                null,
                payload.StartedAt,
                payload.DurationSeconds,
                payload.RecordingUrl),
            ct);

    public async Task<SipoutCallReceiveResult> ReceiveAsteriskCallAsync(
        Guid publicId,
        string? secret,
        AsteriskCallWebhookPayload payload,
        Stream? recording,
        long recordingLength,
        string? recordingFileName,
        string? recordingContentType,
        CancellationToken ct = default)
    {
        if (recording is not null
            && (callRecordingStorage is null
                || recordingLength <= 0
                || recordingLength > callRecordingStorage.MaxUploadBytes))
        {
            return new SipoutCallReceiveResult(
                SipoutCallReceiveOutcome.Invalid,
                Message: "Call recording size is invalid.");
        }

        var contentType = recording is null ? null : NormalizeRecordingContentType(recordingContentType);
        if (recording is not null && contentType is null)
        {
            return new SipoutCallReceiveResult(
                SipoutCallReceiveOutcome.Invalid,
                Message: "Unsupported call recording content type.");
        }

        var result = await ReceiveCallAsync(
            CrmTelephonyProviders.Asterisk,
            publicId,
            secret,
            new SipoutCallWebhookPayload(
                payload.ExternalCallId,
                payload.CallerPhone,
                payload.CalledPhone,
                payload.Direction,
                payload.InternalNumber,
                null,
                payload.StartedAt,
                payload.DurationSeconds,
                null),
            ct);
        if (result.CallId is not Guid callId
            || result.Outcome is SipoutCallReceiveOutcome.Unauthorized or SipoutCallReceiveOutcome.Invalid)
        {
            return result;
        }

        // Unanswered calls have no RTP and therefore no MixMonitor file. Their metadata
        // still belongs in the CRM feed as an incoming/outgoing missed call.
        if (recording is null)
        {
            return result;
        }

        var call = await db.CrmCalls.FirstOrDefaultAsync(x => x.Id == callId, ct);
        if (call is null)
        {
            return new SipoutCallReceiveResult(
                SipoutCallReceiveOutcome.Invalid,
                Message: "Call record was not created.");
        }

        var previousPath = call.RecordingStoragePath;
        var storedPath = await callRecordingStorage!.SaveAsync(callId, recording, ct);
        call.RecordingStoragePath = storedPath;
        call.RecordingContentType = contentType;
        call.RecordingFileName = NormalizeRecordingFileName(recordingFileName, callId, contentType!);
        call.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        if (!string.IsNullOrWhiteSpace(previousPath)
            && !string.Equals(previousPath, storedPath, StringComparison.Ordinal))
        {
            callRecordingStorage.TryDelete(previousPath);
        }

        return result;
    }

    public async Task<AsteriskInboundRouteResult> ResolveAsteriskInboundRouteAsync(
        Guid publicId,
        string? secret,
        string? callerPhone,
        string? calledPhone,
        CancellationToken ct = default)
    {
        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && x.IsEnabled, ct);
        if (receiver is null || !VerifySecret(secret, receiver.SecretHash))
        {
            return new AsteriskInboundRouteResult(AsteriskInboundRouteOutcome.Unauthorized);
        }

        var clientPhone = phoneNormalizer.Normalize(callerPhone ?? string.Empty);
        if (!IsExternalPhone(clientPhone))
        {
            return new AsteriskInboundRouteResult(
                AsteriskInboundRouteOutcome.Invalid,
                Message: "External caller phone is required.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now - CallbackAffinityLifetime;
        var lastOutbound = await db.CrmCalls.AsNoTracking()
            .Where(x => x.OfficeId == receiver.OfficeId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && x.Direction == CrmCallDirections.Outgoing
                && x.ClientPhoneNormalized == clientPhone
                && x.ManagerUserId != null
                && x.StartedAtUtc >= cutoff
                && x.StartedAtUtc <= now.AddMinutes(5))
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => new { x.ManagerUserId, x.StartedAtUtc })
            .FirstOrDefaultAsync(ct);

        string? preferredExtension = null;
        DateTime? affinityExpiresAtUtc = null;
        if (lastOutbound is not null)
        {
            preferredExtension = await db.CrmTelephonyUserBindings.AsNoTracking()
                .Where(x => x.OfficeId == receiver.OfficeId
                    && x.Provider == CrmTelephonyProviders.Asterisk
                    && x.UserId == lastOutbound.ManagerUserId)
                .Select(x => x.ProviderUserKey)
                .FirstOrDefaultAsync(ct);
            if (!IsValidAsteriskExtension(preferredExtension))
            {
                preferredExtension = null;
            }
            else
            {
                affinityExpiresAtUtc = lastOutbound.StartedAtUtc + CallbackAffinityLifetime;
            }
        }

        var activeBindings = await (
            from binding in db.CrmTelephonyUserBindings.AsNoTracking()
            join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
            where binding.OfficeId == receiver.OfficeId
                && binding.Provider == CrmTelephonyProviders.Asterisk
                && profile.OfficeId == receiver.OfficeId
                && profile.CrmShiftActive
            orderby profile.CrmShiftStartedAtUtc, binding.ProviderUserKey
            select new
            {
                binding.ProviderUserKey,
                profile.CrmShiftStartedAtUtc
            })
            .ToListAsync(ct);
        var fallbackExtensions = activeBindings
            .Where(x => CrmShiftRules.IsEffectivelyOnShift(true, x.CrmShiftStartedAtUtc, now))
            .Select(x => x.ProviderUserKey)
            .Where(IsValidAsteriskExtension)
            .Where(x => !string.Equals(x, preferredExtension, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();

        return new AsteriskInboundRouteResult(
            AsteriskInboundRouteOutcome.Resolved,
            preferredExtension,
            fallbackExtensions,
            affinityExpiresAtUtc);
    }

    private async Task<SipoutCallReceiveResult> ReceiveCallAsync(
        string provider,
        Guid publicId,
        string? secret,
        SipoutCallWebhookPayload payload,
        CancellationToken ct)
    {
        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId && x.Provider == provider && x.IsEnabled, ct);
        if (receiver is null || !VerifySecret(secret, receiver.SecretHash))
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Unauthorized);
        }

        var externalCallId = payload.ExternalCallId.Trim();
        if (string.IsNullOrWhiteSpace(externalCallId) || externalCallId.Length > 128)
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Invalid, Message: "External call ID is required.");
        }

        var candidatePhones = new[] { payload.CallerPhone, payload.CalledPhone, payload.LastCaller }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => phoneNormalizer.Normalize(x!))
            .Where(IsExternalPhone)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidatePhones.Count == 0)
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Invalid, Message: "Client phone is missing.");
        }

        var existing = await db.CrmCalls
            .FirstOrDefaultAsync(x => x.OfficeId == receiver.OfficeId
                && x.Provider == provider
                && x.ExternalCallId == externalCallId, ct);
        var card = await FindCardAsync(receiver.OfficeId, candidatePhones, ct);
        var providerUserKey = ResolveProviderUserKey(payload);
        var managerUserId = string.IsNullOrWhiteSpace(providerUserKey)
            ? null
            : await db.CrmTelephonyUserBindings.AsNoTracking()
                .Where(x => x.OfficeId == receiver.OfficeId
                    && x.Provider == provider
                    && x.ProviderUserKey == providerUserKey)
                .Select(x => x.UserId)
                .FirstOrDefaultAsync(ct);
        var direction = NormalizeDirection(payload.CallType, payload.CallerPhone, payload.CalledPhone, providerUserKey);
        var clientPhone = ResolveClientPhone(direction, payload, candidatePhones, providerUserKey);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var startedAt = ParseStartedAt(payload.StartedAt, now);
        var durationSeconds = ParseNonNegativeInt(payload.DurationSeconds);
        var recordingUrl = NormalizeRecordingUrl(payload.RecordingUrl);

        var call = existing ?? new CrmCallEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = receiver.OfficeId,
            Provider = provider,
            ExternalCallId = externalCallId,
            ReceivedAtUtc = now
        };
        call.CardId = card?.Id ?? call.CardId;
        call.Direction = direction;
        call.CallerPhone = payload.CallerPhone?.Trim() ?? string.Empty;
        call.CalledPhone = payload.CalledPhone?.Trim() ?? string.Empty;
        call.ClientPhoneNormalized = clientPhone;
        call.ProviderUserKey = providerUserKey;
        call.ManagerUserId = managerUserId ?? call.ManagerUserId;
        call.StartedAtUtc = startedAt;
        call.DurationSeconds = durationSeconds;
        call.RecordingUrl = recordingUrl ?? call.RecordingUrl;
        if (provider == CrmTelephonyProviders.Plusofon)
        {
            call.NextRecordingFetchAtUtc = call.RecordingUrl is null ? now : null;
        }
        call.UpdatedAtUtc = now;
        if (existing is null)
        {
            db.CrmCalls.Add(call);
        }

        await db.SaveChangesAsync(ct);
        if (call.CardId is Guid)
        {
            panelRealtime?.Notify([PanelChangeKind.Crm], receiver.OfficeId);
        }

        return new SipoutCallReceiveResult(
            call.CardId is null
                ? SipoutCallReceiveOutcome.Unmatched
                : existing is null ? SipoutCallReceiveOutcome.Accepted : SipoutCallReceiveOutcome.Updated,
            call.Id,
            call.CardId);
    }

    private async Task<CrmCandidateCardEntity?> FindCardAsync(
        Guid officeId,
        IReadOnlyCollection<string> candidatePhones,
        CancellationToken ct)
    {
        var direct = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == officeId && candidatePhones.Contains(x.Response.PhoneNormalized))
            .OrderByDescending(x => !x.IsClosed)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (direct is not null)
        {
            return direct;
        }

        return await (
            from card in db.CrmCandidateCards.AsNoTracking()
            join response in db.CandidateResponses.AsNoTracking() on card.ResponseId equals response.Id
            join phone in db.CandidateContactPhones.AsNoTracking() on response.PersonId equals phone.PersonId
            where card.OfficeId == officeId && candidatePhones.Contains(phone.PhoneNormalized)
            orderby !card.IsClosed descending, card.UpdatedAtUtc descending
            select card)
            .FirstOrDefaultAsync(ct);
    }

    private static string ResolveProviderUserKey(SipoutCallWebhookPayload payload)
    {
        var candidates = new[]
        {
            payload.ProviderUserKey,
            IsInternalPhone(payload.LastCaller) ? payload.LastCaller : null,
            IsInternalPhone(payload.CallerPhone) ? payload.CallerPhone : null,
            IsInternalPhone(payload.CalledPhone) ? payload.CalledPhone : null
        };
        return NormalizeProviderUserKey(candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty);
    }

    private static string NormalizeProviderUserKey(string value) => value.Trim().ToLowerInvariant();

    private static bool IsValidSipHost(string value) =>
        value.Length is > 0 and <= 253
        && Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static bool ContainsControlCharacters(string value) =>
        value.Any(char.IsControl) || value.IndexOfAny([';', '[', ']']) >= 0;

    private static string NormalizeProvider(string provider)
    {
        if (!CrmTelephonyProviders.IsSupported(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "Unsupported telephony provider.");
        }
        return CrmTelephonyProviders.Normalize(provider);
    }

    private static bool IsInternalPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 8;
    }

    private static bool IsExternalPhone(string normalized) => normalized.Length >= 10;

    private static bool IsValidAsteriskExtension(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 8
        && value.All(char.IsDigit);

    private sealed record SipProviderCredentialPayload(
        string Server,
        string Domain,
        int Port,
        string Transport,
        string SipLogin,
        string AuthorizationLogin,
        string Password,
        bool UseForOutbound);

    private static string NormalizeDirection(
        string? callType,
        string? callerPhone,
        string? calledPhone,
        string? providerUserKey)
    {
        var value = callType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("out", StringComparison.Ordinal) || value.Contains("исход", StringComparison.Ordinal))
        {
            return CrmCallDirections.Outgoing;
        }
        if (value.Contains("in", StringComparison.Ordinal) || value.Contains("вход", StringComparison.Ordinal))
        {
            return CrmCallDirections.Incoming;
        }
        if (!string.IsNullOrWhiteSpace(providerUserKey))
        {
            if (string.Equals(NormalizeProviderUserKey(callerPhone ?? string.Empty), providerUserKey, StringComparison.Ordinal))
            {
                return CrmCallDirections.Outgoing;
            }
            if (string.Equals(NormalizeProviderUserKey(calledPhone ?? string.Empty), providerUserKey, StringComparison.Ordinal))
            {
                return CrmCallDirections.Incoming;
            }
        }
        return CrmCallDirections.Unknown;
    }

    private string ResolveClientPhone(
        string direction,
        SipoutCallWebhookPayload payload,
        IReadOnlyList<string> candidates,
        string? providerUserKey)
    {
        var preferred = direction == CrmCallDirections.Incoming ? payload.CallerPhone
            : direction == CrmCallDirections.Outgoing ? payload.CalledPhone
            : null;
        var preferredNormalized = string.IsNullOrWhiteSpace(preferred) ? string.Empty : phoneNormalizer.Normalize(preferred);
        if (IsExternalPhone(preferredNormalized))
        {
            return preferredNormalized;
        }

        return candidates.FirstOrDefault(x => !string.Equals(x, providerUserKey, StringComparison.Ordinal))
            ?? candidates[0];
    }

    private static DateTime ParseStartedAt(string? value, DateTime fallbackUtc)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore invalid provider value and use receive time.
            }
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }
        return fallbackUtc;
    }

    private static int ParseNonNegativeInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;

    private static string? NormalizeRecordingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static string? NormalizeRecordingContentType(string? value)
    {
        var normalized = value?.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "audio/wav",
            "audio/mpeg" or "audio/mp3" => "audio/mpeg",
            "audio/ogg" => "audio/ogg",
            _ => null
        };
    }

    private static string NormalizeRecordingFileName(string? value, Guid callId, string contentType)
    {
        var extension = contentType switch
        {
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            _ => ".wav"
        };
        var fileName = Path.GetFileName(value?.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240)
        {
            return $"Звонок-{callId:N}{extension}";
        }
        return Path.ChangeExtension(fileName, extension);
    }

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool VerifySecret(string? secret, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            return expected.Length == actual.Length
                && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
