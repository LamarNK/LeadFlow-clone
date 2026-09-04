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
    CrmSipRuntimeConfigWriter? sipRuntimeConfigWriter = null,
    ICrmNotificationRealtimeNotifier? crmNotificationRealtime = null,
    ILogger<CrmTelephonyService>? logger = null)
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
        IReadOnlyList<CrmSipProviderAccountDto>? sipAccounts = null;
        if (provider == CrmTelephonyProviders.Sipout
            && receiver is not null
            && credentialProtector is not null
            && !string.IsNullOrWhiteSpace(receiver.SipAccountProtected))
        {
            try
            {
                var accounts = ReadSipoutAccounts(receiver);
                var phoneBindings = await (
                    from binding in db.CrmTelephonyUserBindings.AsNoTracking()
                    join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
                    join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
                    where binding.OfficeId == officeId && binding.Provider == CrmTelephonyProviders.Asterisk
                    select new CrmTelephonyUserBindingDto(
                        binding.UserId,
                        string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                        binding.ProviderUserKey,
                        binding.OutboundProvider))
                    .ToListAsync(ct);
                var assignedUsers = phoneBindings
                    .Select(binding => new
                    {
                        Binding = binding,
                        AccountKey = ResolveSipoutAccountKey(binding.OutboundProvider, accounts)
                    })
                    .Where(item => item.AccountKey is not null)
                    .GroupBy(item => item.AccountKey!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Binding, StringComparer.OrdinalIgnoreCase);
                var accountDtos = new List<CrmSipProviderAccountDto>(accounts.Count);
                foreach (var payload in accounts)
                {
                    var runtimeStatus = sipRuntimeConfigWriter is null
                        ? new CrmSipRuntimeStatus("runtime-unavailable", null)
                        : await sipRuntimeConfigWriter.ReadSipoutStatusAsync(officeId, payload.AccountKey, ct);
                    assignedUsers.TryGetValue(payload.AccountKey, out var assignedUser);
                    accountDtos.Add(new CrmSipProviderAccountDto(
                        payload.Server,
                        payload.Domain,
                        payload.Port,
                        payload.Transport,
                        payload.SipLogin,
                        payload.AuthorizationLogin,
                        !string.IsNullOrWhiteSpace(payload.Password),
                        payload.UseForOutbound,
                        runtimeStatus.Status,
                        runtimeStatus.CheckedAtUtc,
                        runtimeStatus.Detail,
                        payload.AccountKey,
                        payload.Name,
                        payload.Mode,
                        assignedUser?.UserId,
                        assignedUser?.UserName,
                        payload.OutboundCallerId,
                        payload.InternalNumber));
                }
                sipAccounts = accountDtos;
                sipAccount = accountDtos.FirstOrDefault(account => account.UseForOutbound)
                    ?? accountDtos.FirstOrDefault(account => account.AccountKey == "default")
                    ?? accountDtos.FirstOrDefault();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                sipAccount = null;
            }
        }
        else if (provider == CrmTelephonyProviders.Beeline
            && receiver is not null
            && credentialProtector is not null
            && !string.IsNullOrWhiteSpace(receiver.ProviderAccessTokenProtected))
        {
            try
            {
                var accounts = ReadBeelineAccounts(receiver);
                var phoneBindings = await (
                    from binding in db.CrmTelephonyUserBindings.AsNoTracking()
                    join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
                    join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
                    where binding.OfficeId == officeId && binding.Provider == CrmTelephonyProviders.Asterisk
                    select new CrmTelephonyUserBindingDto(
                        binding.UserId,
                        string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                        binding.ProviderUserKey,
                        binding.OutboundProvider))
                    .ToListAsync(ct);
                var assignedUsers = phoneBindings
                    .Select(binding => new
                    {
                        Binding = binding,
                        AccountKey = ResolveBeelineAccountKey(binding.OutboundProvider, accounts)
                    })
                    .Where(item => item.AccountKey is not null)
                    .GroupBy(item => item.AccountKey!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Binding, StringComparer.OrdinalIgnoreCase);
                var accountDtos = new List<CrmSipProviderAccountDto>(accounts.Count);
                foreach (var payload in accounts)
                {
                    var runtimeStatus = sipRuntimeConfigWriter is null
                        ? new CrmSipRuntimeStatus("runtime-unavailable", null)
                        : await sipRuntimeConfigWriter.ReadBeelineStatusAsync(officeId, payload.AccountKey, ct);
                    assignedUsers.TryGetValue(payload.AccountKey, out var assignedUser);
                    accountDtos.Add(new CrmSipProviderAccountDto(
                        payload.Server,
                        payload.Domain,
                        payload.Port,
                        payload.Transport,
                        payload.SipLogin,
                        payload.AuthorizationLogin,
                        !string.IsNullOrWhiteSpace(payload.Password),
                        payload.UseForOutbound,
                        runtimeStatus.Status,
                        runtimeStatus.CheckedAtUtc,
                        runtimeStatus.Detail,
                        payload.AccountKey,
                        payload.Name,
                        payload.Mode,
                        assignedUser?.UserId,
                        assignedUser?.UserName));
                }
                sipAccounts = accountDtos;
                sipAccount = accountDtos.FirstOrDefault(account => account.UseForOutbound)
                    ?? accountDtos.FirstOrDefault(account => account.AccountKey == "default")
                    ?? accountDtos.FirstOrDefault();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                sipAccount = null;
            }
        }
        else if (provider == CrmTelephonyProviders.Plusofon
            && receiver is not null
            && credentialProtector is not null
            && !string.IsNullOrWhiteSpace(receiver.SipAccountProtected))
        {
            try
            {
                var accounts = ReadPlusofonAccounts(receiver);
                var phoneBindings = await (
                    from binding in db.CrmTelephonyUserBindings.AsNoTracking()
                    join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
                    join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
                    where binding.OfficeId == officeId && binding.Provider == CrmTelephonyProviders.Asterisk
                    select new CrmTelephonyUserBindingDto(
                        binding.UserId,
                        string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                        binding.ProviderUserKey,
                        binding.OutboundProvider))
                    .ToListAsync(ct);
                var assignedUsers = phoneBindings
                    .Select(binding => new
                    {
                        Binding = binding,
                        AccountKey = ResolvePlusofonAccountKey(binding.OutboundProvider, accounts)
                    })
                    .Where(item => item.AccountKey is not null)
                    .GroupBy(item => item.AccountKey!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Binding, StringComparer.OrdinalIgnoreCase);
                var accountDtos = new List<CrmSipProviderAccountDto>(accounts.Count);
                foreach (var payload in accounts)
                {
                    var runtimeStatus = sipRuntimeConfigWriter is null
                        ? new CrmSipRuntimeStatus("runtime-unavailable", null)
                        : await sipRuntimeConfigWriter.ReadPlusofonStatusAsync(officeId, payload.AccountKey, ct);
                    assignedUsers.TryGetValue(payload.AccountKey, out var assignedUser);
                    accountDtos.Add(new CrmSipProviderAccountDto(
                        payload.Server,
                        payload.Domain,
                        payload.Port,
                        payload.Transport,
                        payload.SipLogin,
                        payload.AuthorizationLogin,
                        !string.IsNullOrWhiteSpace(payload.Password),
                        payload.UseForOutbound,
                        runtimeStatus.Status,
                        runtimeStatus.CheckedAtUtc,
                        runtimeStatus.Detail,
                        payload.AccountKey,
                        payload.Name,
                        payload.Mode,
                        assignedUser?.UserId,
                        assignedUser?.UserName,
                        payload.OutboundCallerId));
                }
                sipAccounts = accountDtos;
                sipAccount = accountDtos.FirstOrDefault(account => account.UseForOutbound)
                    ?? accountDtos.FirstOrDefault(account => account.AccountKey == "default")
                    ?? accountDtos.FirstOrDefault();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                sipAccount = null;
            }
        }

        var credentialsConfigured = provider switch
        {
            CrmTelephonyProviders.Sipout => sipAccount is not null,
            CrmTelephonyProviders.Plusofon => !string.IsNullOrWhiteSpace(receiver?.ProviderClientId)
                && !string.IsNullOrWhiteSpace(receiver?.ProviderAccessTokenProtected),
            CrmTelephonyProviders.Beeline => sipAccount is not null,
            _ => false
        };

        var cloudAccounts = await db.CrmTelephonyProviderAccounts.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == provider)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        var cloudAccountIds = cloudAccounts.Select(x => x.Id).ToList();
        var cloudBindings = cloudAccountIds.Count == 0
            ? new List<CloudAccountBindingRow>()
            : await (
                from binding in db.CrmTelephonyProviderAccountBindings.AsNoTracking()
                join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
                join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
                where cloudAccountIds.Contains(binding.ProviderAccountId)
                select new CloudAccountBindingRow(
                    binding.ProviderAccountId,
                    new CrmTelephonyUserBindingDto(
                        binding.UserId,
                        string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                        binding.ProviderUserKey)))
                .ToListAsync(ct);
        var cloudAccountDtos = cloudAccounts
            .Select(x =>
            {
                var accountBindings = cloudBindings.Where(binding => binding.AccountId == x.Id)
                    .Select(binding => binding.Binding)
                    .OrderBy(binding => binding.UserName)
                    .ToList();
                return CrmTelephonyProviderAccountService.ToDto(x, accountBindings.Count, accountBindings);
            })
            .ToList();

        return new CrmTelephonySettingsDto(
            officeId,
            provider,
            receiver is not null || cloudAccountDtos.Count > 0,
            receiver?.IsEnabled == true || cloudAccountDtos.Any(x => x.IsEnabled),
            receiver?.PublicId,
            bindings,
            credentialsConfigured || cloudAccountDtos.Any(x => x.CredentialsConfigured),
            sipAccount,
            sipAccounts,
            cloudAccountDtos);
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
        var (success, error, _) = await UpsertBeelineSipAccountAsync(officeId, "default", request, ct);
        return (success, error);
    }

    public async Task<(bool Success, string? Error)> SetSipProviderAccountAsync(
        Guid officeId,
        string provider,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        if (provider == CrmTelephonyProviders.Beeline)
        {
            return await SetBeelineSipAccountAsync(officeId, request, ct);
        }
        if (provider == CrmTelephonyProviders.Plusofon)
        {
            return await SetPlusofonSipAccountAsync(officeId, request, ct);
        }
        if (provider == CrmTelephonyProviders.Sipout)
        {
            var (success, error, _) = await UpsertSipoutSipAccountAsync(officeId, "default", request, ct);
            return (success, error);
        }

        return (false, "SIP-аккаунт этого провайдера пока не поддерживается.");
    }

    public async Task<(bool Success, string? Error, string? AccountKey)> UpsertBeelineSipAccountAsync(
        Guid officeId,
        string? accountKey,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct = default)
    {
        if (credentialProtector is null)
        {
            return (false, "Защита реквизитов телефонии недоступна.", null);
        }
        if (sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Общий runtime-каталог API и SIP-сервера не настроен.", null);
        }
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (false, "Офис не найден.", null);
        }

        accountKey = NormalizeSipAccountKey(accountKey);
        if (accountKey is null)
        {
            return (false, "Некорректный идентификатор SIP-линии.", null);
        }
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? accountKey == "default" ? "Общая линия Билайна" : "Линия Билайна"
            : request.Name.Trim();
        var mode = CrmSipAccountModes.IsSupported(request.Mode)
            ? CrmSipAccountModes.Normalize(request.Mode)
            : string.Empty;
        if (name.Length is 0 or > 100 || ContainsControlCharacters(name))
        {
            return (false, "Название линии должно содержать от 1 до 100 символов.", null);
        }
        if (!CrmSipAccountModes.IsSupported(mode))
        {
            return (false, "Выберите общую или персональную SIP-линию.", null);
        }
        if (mode == CrmSipAccountModes.Personal && request.UseForOutbound)
        {
            return (false, "Персональную линию нельзя сделать общей линией офиса по умолчанию.", null);
        }

        var server = request.Server.Trim().ToLowerInvariant();
        var domain = string.IsNullOrWhiteSpace(request.Domain)
            ? server
            : request.Domain.Trim().ToLowerInvariant();
        var transport = request.Transport.Trim().ToLowerInvariant();
        var sipLogin = request.SipLogin.Trim();
        var authorizationLogin = string.IsNullOrWhiteSpace(request.AuthorizationLogin)
            ? sipLogin
            : request.AuthorizationLogin.Trim();
        if (!IsValidSipHost(server) || !IsValidSipHost(domain))
        {
            return (false, "Укажите корректные SIP proxy и домен без протокола sip://.", null);
        }
        if (request.Port is <= 0 or > 65535 || transport is not ("udp" or "tcp"))
        {
            return (false, "Порт должен быть от 1 до 65535, транспорт — UDP или TCP.", null);
        }
        if (!IsValidSipUser(sipLogin, 128) || !IsValidSipUser(authorizationLogin, 256))
        {
            return (false, "Укажите корректные SIP User ID и Authorization User ID.", null);
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Beeline, ct);
        var accounts = new List<SipProviderCredentialPayload>();
        if (receiver is not null && !string.IsNullOrWhiteSpace(receiver.ProviderAccessTokenProtected))
        {
            try
            {
                accounts.AddRange(ReadBeelineAccounts(receiver));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (false, "Сохранённые реквизиты Билайна повреждены. Укажите пароль заново.", null);
            }
        }

        var previous = accounts.FirstOrDefault(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        var password = string.IsNullOrEmpty(request.Password) ? previous?.Password ?? string.Empty : request.Password;
        if (password.Length is 0 or > 4096 || ContainsControlCharacters(password))
        {
            return (false, "Укажите корректный SIP-пароль. При первой настройке он обязателен.", null);
        }

        var payload = new SipProviderCredentialPayload(
            server,
            domain,
            request.Port,
            transport,
            sipLogin,
            authorizationLogin,
            password,
            request.UseForOutbound,
            accountKey,
            name,
            mode);
        accounts.RemoveAll(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (payload.UseForOutbound)
        {
            accounts = accounts.Select(x => x with { UseForOutbound = false }).ToList();
        }
        accounts.Add(payload);
        if (payload.Mode == CrmSipAccountModes.Personal)
        {
            var outboundValue = CrmTelephonyOutboundProviders.ForBeelineLine(accountKey);
            var assignedUsersCount = await db.CrmTelephonyUserBindings.AsNoTracking().CountAsync(x =>
                x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && (x.OutboundProvider == outboundValue
                    || (accountKey == "default" && x.OutboundProvider == CrmTelephonyProviders.Beeline)), ct);
            if (assignedUsersCount > 1)
            {
                return (false, "Эта линия назначена нескольким сотрудникам. Сначала оставьте одного сотрудника, затем смените тип на персональный.", null);
            }
        }
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

        receiver.ProviderClientId = accounts.FirstOrDefault(x => x.UseForOutbound)?.SipLogin ?? accounts[0].SipLogin;
        receiver.ProviderAccessTokenProtected = ProtectBeelineAccounts(accounts);
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        var competingDefaults = payload.UseForOutbound
            ? await ClearCompetingOfficeDefaultsAsync(officeId, CrmTelephonyProviders.Beeline, ct)
            : [];
        await db.SaveChangesAsync(ct);
        await ApplyCompetingDefaultUpdatesAsync(officeId, competingDefaults, ct);
        var (applied, applyError) = await sipRuntimeConfigWriter.WriteBeelineAccountsAsync(
            officeId,
            accounts.Select(ToRuntimeAccount).ToList(),
            ct);
        return applied
            ? (true, null, accountKey)
            : (false, $"Реквизиты сохранены, но SIP-сервер не применил конфигурацию: {applyError}", accountKey);
    }

    private async Task<(bool Success, string? Error)> SetPlusofonSipAccountAsync(
        Guid officeId,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct)
    {
        var (success, error, _) = await UpsertPlusofonSipAccountAsync(officeId, "default", request, ct);
        return (success, error);
    }

    public async Task<(bool Success, string? Error, string? AccountKey)> UpsertPlusofonSipAccountAsync(
        Guid officeId,
        string? accountKey,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct = default)
    {
        if (credentialProtector is null)
        {
            return (false, "Защита реквизитов телефонии недоступна.", null);
        }
        if (sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Общий runtime-каталог API и SIP-сервера не настроен.", null);
        }
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (false, "Офис не найден.", null);
        }

        accountKey = NormalizeSipAccountKey(accountKey);
        if (accountKey is null)
        {
            return (false, "Некорректный идентификатор SIP-линии.", null);
        }
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? accountKey == "default" ? "Общая линия Плюсофона" : "Линия Плюсофона"
            : request.Name.Trim();
        var mode = CrmSipAccountModes.IsSupported(request.Mode)
            ? CrmSipAccountModes.Normalize(request.Mode)
            : string.Empty;
        if (name.Length is 0 or > 100 || ContainsControlCharacters(name))
        {
            return (false, "Название линии должно содержать от 1 до 100 символов.", null);
        }
        if (!CrmSipAccountModes.IsSupported(mode))
        {
            return (false, "Выберите общую или персональную SIP-линию.", null);
        }
        if (mode == CrmSipAccountModes.Personal && request.UseForOutbound)
        {
            return (false, "Персональную линию нельзя сделать общей линией офиса по умолчанию.", null);
        }

        var server = request.Server.Trim().ToLowerInvariant();
        var domain = string.IsNullOrWhiteSpace(request.Domain)
            ? server
            : request.Domain.Trim().ToLowerInvariant();
        var transport = request.Transport.Trim().ToLowerInvariant();
        var sipLogin = request.SipLogin.Trim();
        var authorizationLogin = string.IsNullOrWhiteSpace(request.AuthorizationLogin)
            ? sipLogin
            : request.AuthorizationLogin.Trim();
        var outboundCallerId = phoneNormalizer.Normalize(request.OutboundCallerId ?? string.Empty);
        if (!IsValidSipHost(server) || !IsValidSipHost(domain))
        {
            return (false, "Укажите корректные SIP-сервер и Domain / Realm без протокола sip://.", null);
        }
        if (request.Port is <= 0 or > 65535 || transport is not ("udp" or "tcp"))
        {
            return (false, "Порт должен быть от 1 до 65535, транспорт — UDP или TCP.", null);
        }
        if (!IsValidSipUser(sipLogin, 128) || !IsValidSipUser(authorizationLogin, 256))
        {
            return (false, "Укажите корректные SIP-логин и логин авторизации.", null);
        }
        if (outboundCallerId.Length != 11 || !outboundCallerId.StartsWith('7'))
        {
            return (false, "Укажите исходящий АОН Плюсофона в формате 7XXXXXXXXXX.", null);
        }

        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Plusofon, ct);
        var accounts = new List<SipProviderCredentialPayload>();
        if (!string.IsNullOrWhiteSpace(receiver?.SipAccountProtected))
        {
            try
            {
                accounts.AddRange(ReadPlusofonAccounts(receiver!));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (false, "Сохранённые SIP-реквизиты Плюсофона повреждены. Укажите пароль заново.", null);
            }
        }

        var previous = accounts.FirstOrDefault(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        var password = string.IsNullOrEmpty(request.Password) ? previous?.Password ?? string.Empty : request.Password;
        if (password.Length is 0 or > 4096 || ContainsControlCharacters(password))
        {
            return (false, "Укажите корректный SIP-пароль. При первой настройке он обязателен.", null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var payload = new SipProviderCredentialPayload(
            server,
            domain,
            request.Port,
            transport,
            sipLogin,
            authorizationLogin,
            password,
            request.UseForOutbound,
            AccountKey: accountKey,
            Name: name,
            Mode: mode,
            OutboundCallerId: outboundCallerId);
        accounts.RemoveAll(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (payload.UseForOutbound)
        {
            accounts = accounts.Select(x => x with { UseForOutbound = false }).ToList();
        }
        accounts.Add(payload);
        if (payload.Mode == CrmSipAccountModes.Personal)
        {
            var outboundValue = CrmTelephonyOutboundProviders.ForPlusofonLine(accountKey);
            var assignedUsersCount = await db.CrmTelephonyUserBindings.AsNoTracking().CountAsync(x =>
                x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && (x.OutboundProvider == outboundValue
                    || (accountKey == "default" && x.OutboundProvider == CrmTelephonyProviders.Plusofon)), ct);
            if (assignedUsersCount > 1)
            {
                return (false, "Эта линия назначена нескольким сотрудникам. Сначала оставьте одного сотрудника, затем смените тип на персональный.", null);
            }
        }
        if (receiver is null)
        {
            receiver = new CrmTelephonyWebhookEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = CrmTelephonyProviders.Plusofon,
                PublicId = Guid.NewGuid(),
                SecretHash = HashSecret(ToBase64Url(RandomNumberGenerator.GetBytes(32))),
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }

        receiver.SipAccountProtected = ProtectPlusofonAccounts(accounts);
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        var competingDefaults = payload.UseForOutbound
            ? await ClearCompetingOfficeDefaultsAsync(officeId, CrmTelephonyProviders.Plusofon, ct)
            : [];
        await db.SaveChangesAsync(ct);
        await ApplyCompetingDefaultUpdatesAsync(officeId, competingDefaults, ct);
        var (applied, applyError) = await sipRuntimeConfigWriter.WritePlusofonAccountsAsync(
            officeId,
            accounts.Select(ToRuntimeAccount).ToList(),
            ct);
        return applied
            ? (true, null, accountKey)
            : (false, $"Реквизиты сохранены, но SIP-сервер не применил конфигурацию: {applyError}", accountKey);
    }

    public async Task<(bool Success, string? Error)> DeleteBeelineSipAccountAsync(
        Guid officeId,
        string accountKey,
        CancellationToken ct = default)
    {
        if (credentialProtector is null || sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Настройка SIP-сервера недоступна.");
        }
        accountKey = NormalizeSipAccountKey(accountKey) ?? string.Empty;
        if (accountKey.Length == 0)
        {
            return (false, "Некорректный идентификатор SIP-линии.");
        }
        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Beeline, ct);
        if (receiver is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        List<SipProviderCredentialPayload> accounts;
        try
        {
            accounts = ReadBeelineAccounts(receiver).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return (false, "Сохранённые реквизиты Билайна повреждены.");
        }
        var accountToRemove = accounts.FirstOrDefault(x =>
            string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (accountToRemove is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        var outboundProviders = await db.CrmTelephonyUserBindings.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Asterisk)
            .Select(x => x.OutboundProvider)
            .ToListAsync(ct);
        var isAssigned = outboundProviders.Any(outboundProvider => string.Equals(
            ResolveBeelineAccountKey(outboundProvider, accounts),
            accountKey,
            StringComparison.OrdinalIgnoreCase));
        if (isAssigned)
        {
            return (false, "Сначала назначьте сотрудникам другую исходящую линию.");
        }

        accounts.Remove(accountToRemove);

        receiver.ProviderClientId = accounts.FirstOrDefault(x => x.UseForOutbound)?.SipLogin
            ?? accounts.FirstOrDefault()?.SipLogin;
        receiver.ProviderAccessTokenProtected = accounts.Count == 0 ? null : ProtectBeelineAccounts(accounts);
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var (applied, applyError) = await sipRuntimeConfigWriter.WriteBeelineAccountsAsync(
            officeId, accounts.Select(ToRuntimeAccount).ToList(), ct);
        return applied ? (true, null) : (false, $"Линия удалена, но SIP-сервер не применил конфигурацию: {applyError}");
    }

    public async Task<(bool Success, string? Error, string? AccountKey)> UpsertSipoutSipAccountAsync(
        Guid officeId,
        string? accountKey,
        UpdateSipProviderAccountRequest request,
        CancellationToken ct = default)
    {
        if (credentialProtector is null)
        {
            return (false, "Защита реквизитов телефонии недоступна.", null);
        }
        if (sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Общий runtime-каталог API и SIP-сервера не настроен.", null);
        }
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (false, "Офис не найден.", null);
        }

        accountKey = NormalizeSipAccountKey(accountKey);
        if (accountKey is null)
        {
            return (false, "Некорректный идентификатор SIP-линии.", null);
        }
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? accountKey == "default" ? "Общая линия SIPOUT" : "Линия SIPOUT"
            : request.Name.Trim();
        var mode = CrmSipAccountModes.IsSupported(request.Mode)
            ? CrmSipAccountModes.Normalize(request.Mode)
            : string.Empty;
        if (name.Length is 0 or > 100 || ContainsControlCharacters(name))
        {
            return (false, "Название линии должно содержать от 1 до 100 символов.", null);
        }
        if (!CrmSipAccountModes.IsSupported(mode))
        {
            return (false, "Выберите общую или персональную SIP-линию.", null);
        }
        if (mode == CrmSipAccountModes.Personal && request.UseForOutbound)
        {
            return (false, "Персональную линию нельзя сделать общей линией офиса по умолчанию.", null);
        }

        var server = string.IsNullOrWhiteSpace(request.Server)
            ? "sip.sipout.net"
            : request.Server.Trim().ToLowerInvariant();
        var domain = string.IsNullOrWhiteSpace(request.Domain)
            ? server
            : request.Domain.Trim().ToLowerInvariant();
        var transport = request.Transport.Trim().ToLowerInvariant();
        var sipLogin = request.SipLogin.Trim();
        var authorizationLogin = string.IsNullOrWhiteSpace(request.AuthorizationLogin)
            ? sipLogin
            : request.AuthorizationLogin.Trim();
        var internalNumber = new string((request.InternalNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var outboundCallerId = phoneNormalizer.Normalize(request.OutboundCallerId ?? string.Empty);
        if (!IsValidSipHost(server) || !IsValidSipHost(domain))
        {
            return (false, "Укажите корректные SIP-сервер и Domain / Realm без протокола sip://.", null);
        }
        if (request.Port is <= 0 or > 65535 || transport is not ("udp" or "tcp"))
        {
            return (false, "Порт должен быть от 1 до 65535, транспорт — UDP или TCP.", null);
        }
        if (!IsValidSipUser(sipLogin, 128) || !IsValidSipUser(authorizationLogin, 256))
        {
            return (false, "Укажите корректные SIP-логин и логин авторизации.", null);
        }
        if (internalNumber.Length is < 1 or > 8)
        {
            return (false, "Внутренний номер SIPOUT должен содержать от 1 до 8 цифр.", null);
        }
        if (outboundCallerId.Length != 11 || !outboundCallerId.StartsWith('7'))
        {
            return (false, "Укажите исходящий АОН SIPOUT в формате 7XXXXXXXXXX.", null);
        }

        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Sipout, ct);
        var accounts = new List<SipProviderCredentialPayload>();
        if (!string.IsNullOrWhiteSpace(receiver?.SipAccountProtected))
        {
            try
            {
                accounts.AddRange(ReadSipoutAccounts(receiver!));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (false, "Сохранённые SIP-реквизиты SIPOUT повреждены. Укажите пароль заново.", null);
            }
        }

        var previous = accounts.FirstOrDefault(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        var password = string.IsNullOrEmpty(request.Password) ? previous?.Password ?? string.Empty : request.Password;
        if (password.Length is 0 or > 4096 || ContainsControlCharacters(password))
        {
            return (false, "Укажите корректный SIP-пароль. При первой настройке он обязателен.", null);
        }
        if (accounts.Any(x => !string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.InternalNumber, internalNumber, StringComparison.Ordinal)))
        {
            return (false, "Этот внутренний номер SIPOUT уже используется другой линией офиса.", null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var payload = new SipProviderCredentialPayload(
            server,
            domain,
            request.Port,
            transport,
            sipLogin,
            authorizationLogin,
            password,
            request.UseForOutbound,
            AccountKey: accountKey,
            Name: name,
            Mode: mode,
            OutboundCallerId: outboundCallerId,
            InternalNumber: internalNumber);
        accounts.RemoveAll(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (payload.UseForOutbound)
        {
            accounts = accounts.Select(x => x with { UseForOutbound = false }).ToList();
        }
        accounts.Add(payload);
        if (payload.Mode == CrmSipAccountModes.Personal)
        {
            var outboundValue = CrmTelephonyOutboundProviders.ForSipoutLine(accountKey);
            var assignedUsersCount = await db.CrmTelephonyUserBindings.AsNoTracking().CountAsync(x =>
                x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && (x.OutboundProvider == outboundValue
                    || (accountKey == "default" && x.OutboundProvider == CrmTelephonyProviders.Sipout)), ct);
            if (assignedUsersCount > 1)
            {
                return (false, "Эта линия назначена нескольким сотрудникам. Сначала оставьте одного сотрудника, затем смените тип на персональный.", null);
            }
        }

        if (receiver is null)
        {
            receiver = new CrmTelephonyWebhookEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = CrmTelephonyProviders.Sipout,
                PublicId = Guid.NewGuid(),
                SecretHash = HashSecret(ToBase64Url(RandomNumberGenerator.GetBytes(32))),
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }
        receiver.SipAccountProtected = ProtectSipoutAccounts(accounts);
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        var competingDefaults = payload.UseForOutbound
            ? await ClearCompetingOfficeDefaultsAsync(officeId, CrmTelephonyProviders.Sipout, ct)
            : [];
        await db.SaveChangesAsync(ct);
        await ApplyCompetingDefaultUpdatesAsync(officeId, competingDefaults, ct);

        var (applied, applyError) = await sipRuntimeConfigWriter.WriteSipoutAccountsAsync(
            officeId, accounts.Select(ToRuntimeAccount).ToList(), ct);
        return applied
            ? (true, null, accountKey)
            : (false, $"Реквизиты сохранены, но SIP-сервер не применил конфигурацию: {applyError}", accountKey);
    }

    public async Task<(bool Success, string? Error)> DeletePlusofonSipAccountAsync(
        Guid officeId,
        string accountKey,
        CancellationToken ct = default)
    {
        if (credentialProtector is null || sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Настройка SIP-сервера недоступна.");
        }
        accountKey = NormalizeSipAccountKey(accountKey) ?? string.Empty;
        if (accountKey.Length == 0)
        {
            return (false, "Некорректный идентификатор SIP-линии.");
        }
        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Plusofon, ct);
        if (receiver is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        List<SipProviderCredentialPayload> accounts;
        try
        {
            accounts = ReadPlusofonAccounts(receiver).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return (false, "Сохранённые реквизиты Плюсофона повреждены.");
        }
        var accountToRemove = accounts.FirstOrDefault(x =>
            string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (accountToRemove is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        var outboundProviders = await db.CrmTelephonyUserBindings.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Asterisk)
            .Select(x => x.OutboundProvider)
            .ToListAsync(ct);
        var isAssigned = outboundProviders.Any(outboundProvider => string.Equals(
            ResolvePlusofonAccountKey(outboundProvider, accounts),
            accountKey,
            StringComparison.OrdinalIgnoreCase));
        if (isAssigned)
        {
            return (false, "Сначала назначьте сотрудникам другую исходящую линию.");
        }

        accounts.Remove(accountToRemove);
        receiver.SipAccountProtected = accounts.Count == 0 ? null : ProtectPlusofonAccounts(accounts);
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var (applied, applyError) = await sipRuntimeConfigWriter.WritePlusofonAccountsAsync(
            officeId, accounts.Select(ToRuntimeAccount).ToList(), ct);
        return applied ? (true, null) : (false, $"Линия удалена, но SIP-сервер не применил конфигурацию: {applyError}");
    }

    public async Task<(bool Success, string? Error)> DeleteSipoutSipAccountAsync(
        Guid officeId,
        string accountKey,
        CancellationToken ct = default)
    {
        if (credentialProtector is null || sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Настройка SIP-сервера недоступна.");
        }
        accountKey = NormalizeSipAccountKey(accountKey) ?? string.Empty;
        if (accountKey.Length == 0)
        {
            return (false, "Некорректный идентификатор SIP-линии.");
        }
        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Sipout, ct);
        if (receiver is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        List<SipProviderCredentialPayload> accounts;
        try
        {
            accounts = ReadSipoutAccounts(receiver).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return (false, "Сохранённые реквизиты SIPOUT повреждены.");
        }
        var accountToRemove = accounts.FirstOrDefault(x =>
            string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (accountToRemove is null)
        {
            return (false, "SIP-линия не найдена.");
        }

        var outboundProviders = await db.CrmTelephonyUserBindings.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Asterisk)
            .Select(x => x.OutboundProvider)
            .ToListAsync(ct);
        var isAssigned = outboundProviders.Any(outboundProvider => string.Equals(
            ResolveSipoutAccountKey(outboundProvider, accounts),
            accountKey,
            StringComparison.OrdinalIgnoreCase));
        if (isAssigned)
        {
            return (false, "Сначала назначьте сотрудникам другую исходящую линию.");
        }

        accounts.Remove(accountToRemove);
        receiver.SipAccountProtected = accounts.Count == 0 ? null : ProtectSipoutAccounts(accounts);
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var (applied, applyError) = await sipRuntimeConfigWriter.WriteSipoutAccountsAsync(
            officeId, accounts.Select(ToRuntimeAccount).ToList(), ct);
        return applied ? (true, null) : (false, $"Линия удалена, но SIP-сервер не применил конфигурацию: {applyError}");
    }

    public async Task<(bool Success, string? Error)> SetOfficeDefaultOutboundAsync(
        Guid officeId,
        string? outboundProvider,
        CancellationToken ct = default)
    {
        if (credentialProtector is null || sipRuntimeConfigWriter is null || !sipRuntimeConfigWriter.IsAvailable)
        {
            return (false, "Настройка SIP-сервера недоступна.");
        }

        var normalized = CrmTelephonyOutboundProviders.Normalize(outboundProvider);
        string provider;
        string accountKey;
        if (CrmTelephonyOutboundProviders.TryGetSipoutLineKey(normalized, out accountKey))
        {
            provider = CrmTelephonyProviders.Sipout;
        }
        else if (CrmTelephonyOutboundProviders.TryGetPlusofonLineKey(normalized, out accountKey))
        {
            provider = CrmTelephonyProviders.Plusofon;
        }
        else if (CrmTelephonyOutboundProviders.TryGetBeelineLineKey(normalized, out accountKey))
        {
            provider = CrmTelephonyProviders.Beeline;
        }
        else
        {
            return (false, "Выберите подключённую общую линию SIPOUT, Плюсофона или Билайна.");
        }

        var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == provider, ct);
        if (receiver is null)
        {
            return (false, "Выбранная SIP-линия не настроена в этом офисе.");
        }

        List<SipProviderCredentialPayload> accounts;
        try
        {
            accounts = ReadProviderAccounts(provider, receiver).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return (false, "Не удалось прочитать защищённые реквизиты выбранной линии.");
        }

        var selected = accounts.FirstOrDefault(account =>
            string.Equals(account.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return (false, "Выбранная SIP-линия не найдена.");
        }
        if (selected.Mode != CrmSipAccountModes.Shared)
        {
            return (false, "Персональную одноканальную линию нельзя назначить линией всего офиса.");
        }

        accounts = accounts.Select(account => account with
        {
            UseForOutbound = string.Equals(account.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        StoreProviderAccounts(provider, receiver, accounts);
        receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        IReadOnlyList<CompetingDefaultUpdate> competingDefaults;
        try
        {
            competingDefaults = await ClearCompetingOfficeDefaultsAsync(officeId, provider, ct);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return (false, "Не удалось прочитать настройки другого провайдера.");
        }
        await db.SaveChangesAsync(ct);

        var competingApplyError = await ApplyCompetingDefaultUpdatesAsync(officeId, competingDefaults, ct);
        var result = await WriteProviderAccountsAsync(provider, officeId, accounts, ct);
        if (!result.Success)
        {
            return (false, $"Выбор сохранён, но Asterisk не применил маршрут: {result.Error}");
        }
        return competingApplyError is null
            ? (true, null)
            : (false, $"Маршрут переключён, но Asterisk не обновил другой провайдер: {competingApplyError}");
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
                IsEnabled = false,
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }

        receiver.PublicId = publicId;
        receiver.SecretHash = HashSecret(secret);
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

        SipProviderCredentialPayload? selectedBeelineAccount = null;
        SipProviderCredentialPayload? selectedPlusofonAccount = null;
        SipProviderCredentialPayload? selectedSipoutAccount = null;
        var normalizedOutboundProvider = CrmTelephonyOutboundProviders.Normalize(outboundProvider);
        if (provider == CrmTelephonyProviders.Asterisk
            && CrmTelephonyOutboundProviders.TryGetSipoutLineKey(normalizedOutboundProvider, out var sipoutAccountKey))
        {
            var sipoutReceiver = await db.CrmTelephonyWebhooks.AsNoTracking().FirstOrDefaultAsync(x =>
                x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Sipout, ct);
            if (sipoutReceiver is null || credentialProtector is null)
            {
                return (null, "Выбранная линия SIPOUT не настроена в этом офисе.");
            }
            try
            {
                selectedSipoutAccount = ReadSipoutAccounts(sipoutReceiver).FirstOrDefault(x =>
                    string.Equals(x.AccountKey, sipoutAccountKey, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (null, "Не удалось прочитать настройки выбранной линии SIPOUT.");
            }
            if (selectedSipoutAccount is null)
            {
                return (null, "Выбранная линия SIPOUT не найдена.");
            }
            if (selectedSipoutAccount.Mode == CrmSipAccountModes.Personal)
            {
                var personalLineBusy = await db.CrmTelephonyUserBindings.AsNoTracking().AnyAsync(x =>
                    x.OfficeId == officeId
                    && x.Provider == CrmTelephonyProviders.Asterisk
                    && x.UserId != userId
                    && (x.OutboundProvider == normalizedOutboundProvider
                        || (sipoutAccountKey == "default"
                            && x.OutboundProvider == CrmTelephonyProviders.Sipout)), ct);
                if (personalLineBusy)
                {
                    return (null, "Персональная линия SIPOUT уже назначена другому сотруднику.");
                }
            }
        }
        else if (provider == CrmTelephonyProviders.Asterisk
            && CrmTelephonyOutboundProviders.TryGetBeelineLineKey(normalizedOutboundProvider, out var beelineAccountKey))
        {
            var beelineReceiver = await db.CrmTelephonyWebhooks.AsNoTracking().FirstOrDefaultAsync(x =>
                x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Beeline, ct);
            if (beelineReceiver is null || credentialProtector is null)
            {
                return (null, "Выбранная линия Билайна не настроена в этом офисе.");
            }
            try
            {
                selectedBeelineAccount = ReadBeelineAccounts(beelineReceiver).FirstOrDefault(x =>
                    string.Equals(x.AccountKey, beelineAccountKey, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (null, "Не удалось прочитать настройки выбранной линии Билайна.");
            }
            if (selectedBeelineAccount is null)
            {
                return (null, "Выбранная линия Билайна не найдена.");
            }
            if (selectedBeelineAccount.Mode == CrmSipAccountModes.Personal)
            {
                var personalLineBusy = await db.CrmTelephonyUserBindings.AsNoTracking().AnyAsync(x =>
                    x.OfficeId == officeId
                    && x.Provider == CrmTelephonyProviders.Asterisk
                    && x.UserId != userId
                    && (x.OutboundProvider == normalizedOutboundProvider
                        || (beelineAccountKey == "default"
                            && x.OutboundProvider == CrmTelephonyProviders.Beeline)), ct);
                if (personalLineBusy)
                {
                    return (null, "Персональная линия Билайна уже назначена другому сотруднику.");
                }
            }
        }
        else if (provider == CrmTelephonyProviders.Asterisk
            && CrmTelephonyOutboundProviders.TryGetPlusofonLineKey(normalizedOutboundProvider, out var plusofonAccountKey))
        {
            var plusofonReceiver = await db.CrmTelephonyWebhooks.AsNoTracking().FirstOrDefaultAsync(x =>
                x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Plusofon, ct);
            if (plusofonReceiver is null || credentialProtector is null)
            {
                return (null, "Выбранная линия Плюсофона не настроена в этом офисе.");
            }
            try
            {
                selectedPlusofonAccount = ReadPlusofonAccounts(plusofonReceiver).FirstOrDefault(x =>
                    string.Equals(x.AccountKey, plusofonAccountKey, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return (null, "Не удалось прочитать настройки выбранной линии Плюсофона.");
            }
            if (selectedPlusofonAccount is null)
            {
                return (null, "Выбранная линия Плюсофона не найдена.");
            }
            if (selectedPlusofonAccount.Mode == CrmSipAccountModes.Personal)
            {
                var personalLineBusy = await db.CrmTelephonyUserBindings.AsNoTracking().AnyAsync(x =>
                    x.OfficeId == officeId
                    && x.Provider == CrmTelephonyProviders.Asterisk
                    && x.UserId != userId
                    && (x.OutboundProvider == normalizedOutboundProvider
                        || (plusofonAccountKey == "default"
                            && x.OutboundProvider == CrmTelephonyProviders.Plusofon)), ct);
                if (personalLineBusy)
                {
                    return (null, "Персональная линия Плюсофона уже назначена другому сотруднику.");
                }
            }
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
            ? normalizedOutboundProvider
            : CrmTelephonyOutboundProviders.Default;
        binding.UpdatedAtUtc = now;
        if (provider == CrmTelephonyProviders.Asterisk && credentialProtector is not null)
        {
            EnsureWebRtcCredentials(binding);
        }
        await db.SaveChangesAsync(ct);
        if (provider == CrmTelephonyProviders.Asterisk)
        {
            await PublishAsteriskRuntimeAsync(officeId, ct);
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
            await PublishAsteriskRuntimeAsync(officeId, ct);
        }
        return true;
    }

    public async Task<(CrmAsteriskWebRtcEndpoint? Endpoint, string? Error)> GetOrProvisionWebRtcEndpointAsync(
        Guid officeId,
        string userId,
        CancellationToken ct = default)
    {
        if (credentialProtector is null)
        {
            return (null, "Хранилище реквизитов браузерной телефонии недоступно.");
        }

        var binding = await db.CrmTelephonyUserBindings.FirstOrDefaultAsync(x =>
            x.OfficeId == officeId
            && x.Provider == CrmTelephonyProviders.Asterisk
            && x.UserId == userId, ct);
        if (binding is null)
        {
            return (null, "Для сотрудника не настроен внутренний номер.");
        }

        var (endpoint, changed) = EnsureWebRtcCredentials(binding);
        if (changed)
        {
            binding.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await PublishWebRtcEndpointsAsync(officeId, ct);
        }
        return (endpoint, null);
    }

    public async Task<int> SynchronizeAsteriskWebRtcAsync(CancellationToken ct = default)
    {
        if (credentialProtector is null
            || sipRuntimeConfigWriter is null
            || !sipRuntimeConfigWriter.IsAvailable)
        {
            return 0;
        }

        var bindings = await db.CrmTelephonyUserBindings
            .Where(x => x.Provider == CrmTelephonyProviders.Asterisk)
            .ToListAsync(ct);
        var changed = false;
        foreach (var binding in bindings)
        {
            var (_, bindingChanged) = EnsureWebRtcCredentials(binding);
            if (!bindingChanged)
            {
                continue;
            }
            binding.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            changed = true;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        foreach (var officeId in bindings.Select(x => x.OfficeId).Distinct())
        {
            await PublishAsteriskRuntimeAsync(officeId, ct);
        }
        return bindings.Count;
    }

    private async Task PublishAsteriskRuntimeAsync(Guid officeId, CancellationToken ct)
    {
        await PublishUserOutboundRoutesAsync(officeId, ct);
        await PublishWebRtcEndpointsAsync(officeId, ct);
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

    private async Task PublishWebRtcEndpointsAsync(Guid officeId, CancellationToken ct)
    {
        if (credentialProtector is null
            || sipRuntimeConfigWriter is null
            || !sipRuntimeConfigWriter.IsAvailable)
        {
            return;
        }

        var bindings = await db.CrmTelephonyUserBindings.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == CrmTelephonyProviders.Asterisk)
            .OrderBy(x => x.ProviderUserKey)
            .ToListAsync(ct);
        var endpoints = new List<CrmAsteriskWebRtcEndpoint>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.WebRtcAuthorizationUsername)
                || string.IsNullOrWhiteSpace(binding.WebRtcPasswordProtected))
            {
                continue;
            }
            try
            {
                endpoints.Add(new CrmAsteriskWebRtcEndpoint(
                    binding.ProviderUserKey,
                    binding.WebRtcAuthorizationUsername,
                    credentialProtector.Unprotect(binding.WebRtcPasswordProtected)));
            }
            catch (CryptographicException)
            {
                // A subsequent synchronization regenerates an unreadable secret.
            }
        }
        await sipRuntimeConfigWriter.WriteWebRtcEndpointsAsync(officeId, endpoints, ct);
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
                null,
                payload.Disposition,
                payload.DialStatus,
                payload.HangupCause),
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

    public Task<AsteriskInboundRouteResult> ResolveAsteriskInboundRouteAsync(
        Guid publicId,
        string? secret,
        string? callerPhone,
        string? calledPhone,
        CancellationToken ct = default) =>
        ResolveAsteriskInboundRouteAsync(
            publicId,
            secret,
            callerPhone,
            calledPhone,
            inboundProvider: null,
            inboundAccountKey: null,
            ct);

    public async Task<AsteriskInboundRouteResult> ResolveAsteriskInboundRouteAsync(
        Guid publicId,
        string? secret,
        string? callerPhone,
        string? calledPhone,
        string? inboundProvider,
        string? inboundAccountKey,
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
        var card = await FindCardAsync(receiver.OfficeId, [clientPhone], ct);
        if (!string.IsNullOrWhiteSpace(card?.ManagerUserId))
        {
            var responsibleExtension = await db.CrmTelephonyUserBindings.AsNoTracking()
                .Where(x => x.OfficeId == receiver.OfficeId
                    && x.Provider == CrmTelephonyProviders.Asterisk
                    && x.UserId == card.ManagerUserId)
                .Select(x => x.ProviderUserKey)
                .FirstOrDefaultAsync(ct);
            if (!IsValidAsteriskExtension(responsibleExtension))
            {
                responsibleExtension = null;
            }

            // A manager cannot work with another manager's card. Keep the route
            // exclusive even when the responsible user has no active SIP contact
            // or no Asterisk binding, so the dialplan never leaks the call to a
            // fallback employee or the office default extension.
            return new AsteriskInboundRouteResult(
                AsteriskInboundRouteOutcome.Resolved,
                responsibleExtension,
                [],
                IsExclusive: true);
        }

        string? preferredExtension = await ResolvePersonalInboundExtensionAsync(
            receiver.OfficeId,
            inboundProvider,
            inboundAccountKey,
            ct);
        if (preferredExtension is not null)
        {
            // An unknown caller to a personal provider line belongs to that
            // line's assigned manager. Do not leak the call to another manager
            // when the owner is offline or does not answer.
            return new AsteriskInboundRouteResult(
                AsteriskInboundRouteOutcome.Resolved,
                preferredExtension,
                [],
                IsExclusive: true);
        }

        DateTime? affinityExpiresAtUtc = null;
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

    private async Task<string?> ResolvePersonalInboundExtensionAsync(
        Guid officeId,
        string? inboundProvider,
        string? inboundAccountKey,
        CancellationToken ct)
    {
        if (!CrmTelephonyProviders.IsSupported(inboundProvider)
            || inboundProvider is not (CrmTelephonyProviders.Sipout or CrmTelephonyProviders.Plusofon or CrmTelephonyProviders.Beeline))
        {
            return null;
        }

        var accountKey = NormalizeSipAccountKey(inboundAccountKey, generateWhenEmpty: false);
        if (accountKey is null || credentialProtector is null)
        {
            return null;
        }

        var provider = CrmTelephonyProviders.Normalize(inboundProvider);
        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking().FirstOrDefaultAsync(x =>
            x.OfficeId == officeId && x.Provider == provider && x.IsEnabled, ct);
        if (receiver is null)
        {
            return null;
        }

        IReadOnlyList<SipProviderCredentialPayload> accounts;
        try
        {
            accounts = ReadProviderAccounts(provider, receiver);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }

        var account = accounts.FirstOrDefault(x =>
            string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        if (account?.Mode != CrmSipAccountModes.Personal)
        {
            return null;
        }

        var outboundProvider = provider switch
        {
            CrmTelephonyProviders.Sipout => CrmTelephonyOutboundProviders.ForSipoutLine(accountKey),
            CrmTelephonyProviders.Plusofon => CrmTelephonyOutboundProviders.ForPlusofonLine(accountKey),
            _ => CrmTelephonyOutboundProviders.ForBeelineLine(accountKey)
        };
        var extension = await db.CrmTelephonyUserBindings.AsNoTracking()
            .Where(x => x.OfficeId == officeId
                && x.Provider == CrmTelephonyProviders.Asterisk
                && (x.OutboundProvider == outboundProvider
                    || (accountKey == "default" && x.OutboundProvider == provider)))
            .Select(x => x.ProviderUserKey)
            .FirstOrDefaultAsync(ct);
        return IsValidAsteriskExtension(extension) ? extension : null;
    }

    private async Task<SipoutCallReceiveResult> ReceiveCallAsync(
        string provider,
        Guid publicId,
        string? secret,
        SipoutCallWebhookPayload payload,
        CancellationToken ct)
    {
        var providerAccount = await db.CrmTelephonyProviderAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId && x.Provider == provider && x.IsEnabled, ct);
        if (providerAccount is not null)
        {
            if (!VerifySecret(secret, providerAccount.SecretHash))
            {
                return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Unauthorized);
            }
            return await ProcessReceivedCallAsync(providerAccount.OfficeId, provider, providerAccount, payload, ct);
        }

        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId && x.Provider == provider && x.IsEnabled, ct);
        if (receiver is null || !VerifySecret(secret, receiver.SecretHash))
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Unauthorized);
        }

        return await ProcessReceivedCallAsync(receiver.OfficeId, provider, null, payload, ct);
    }

    internal async Task<SipoutCallReceiveResult> ReceiveProviderAccountCallAsync(
        Guid providerAccountId,
        SipoutCallWebhookPayload payload,
        CancellationToken ct = default)
    {
        var account = await db.CrmTelephonyProviderAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == providerAccountId && x.IsEnabled, ct);
        return account is null
            ? new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Unauthorized)
            : await ProcessReceivedCallAsync(account.OfficeId, account.Provider, account, payload, ct);
    }

    internal async Task<int> ReconcileUnmatchedCallsAsync(CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-180);
        var calls = await db.CrmCalls
            .Where(x => x.CardId == null
                && x.StartedAtUtc >= cutoff
                && x.ClientPhoneNormalized != "")
            .OrderByDescending(x => x.StartedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        var matched = 0;
        foreach (var call in calls)
        {
            var card = await FindCardAsync(call.OfficeId, [call.ClientPhoneNormalized], ct);
            if (card is null) continue;
            call.CardId = card.Id;
            call.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            matched++;
            panelRealtime?.Notify([PanelChangeKind.Crm], call.OfficeId);
        }
        if (matched > 0) await db.SaveChangesAsync(ct);
        return matched;
    }

    private async Task<SipoutCallReceiveResult> ProcessReceivedCallAsync(
        Guid officeId,
        string provider,
        CrmTelephonyProviderAccountEntity? providerAccount,
        SipoutCallWebhookPayload payload,
        CancellationToken ct)
    {

        var externalCallId = payload.ExternalCallId.Trim();
        if (string.IsNullOrWhiteSpace(externalCallId) || externalCallId.Length > 128)
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Invalid, Message: "External call ID is required.");
        }

        var providerUserKey = ResolveProviderUserKey(payload);
        var direction = NormalizeDirection(payload.CallType, payload.CallerPhone, payload.CalledPhone, providerUserKey);
        var ownedNumbers = providerAccount is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : CrmTelephonyProviderAccountService.DeserializeOwnedNumbers(providerAccount.OwnedNumbersJson)
                .ToHashSet(StringComparer.Ordinal);
        var preferredPhones = direction == CrmCallDirections.Incoming
            ? new[] { payload.CallerPhone, payload.LastCaller, payload.CalledPhone }
            : direction == CrmCallDirections.Outgoing
                ? new[] { payload.CalledPhone, payload.LastCaller, payload.CallerPhone }
                : new[] { payload.CallerPhone, payload.CalledPhone, payload.LastCaller };
        var candidatePhones = preferredPhones
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => phoneNormalizer.Normalize(x!))
            .Where(IsExternalPhone)
            .Where(x => !ownedNumbers.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidatePhones.Count == 0)
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Invalid, Message: "Client phone is missing.");
        }

        var providerAccountId = providerAccount?.Id;
        var existing = await db.CrmCalls.FirstOrDefaultAsync(x =>
            x.ExternalCallId == externalCallId
            && (providerAccountId != null
                ? x.ProviderAccountId == providerAccountId
                : x.ProviderAccountId == null && x.OfficeId == officeId && x.Provider == provider), ct);
        var card = await FindCardAsync(officeId, candidatePhones, ct);
        string? managerUserId = null;
        if (!string.IsNullOrWhiteSpace(providerUserKey))
        {
            managerUserId = providerAccountId is Guid accountId
                ? await db.CrmTelephonyProviderAccountBindings.AsNoTracking()
                    .Where(x => x.ProviderAccountId == accountId && x.ProviderUserKey == providerUserKey)
                    .Select(x => x.UserId)
                    .FirstOrDefaultAsync(ct)
                : await db.CrmTelephonyUserBindings.AsNoTracking()
                    .Where(x => x.OfficeId == officeId
                        && x.Provider == provider
                        && x.ProviderUserKey == providerUserKey)
                    .Select(x => x.UserId)
                    .FirstOrDefaultAsync(ct);
        }
        var clientPhone = ResolveClientPhone(direction, payload, candidatePhones, providerUserKey);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var startedAt = ParseStartedAt(payload.StartedAt, now);
        var durationSeconds = ParseNonNegativeInt(payload.DurationSeconds);
        var recordingUrl = NormalizeRecordingUrl(payload.RecordingUrl);
        var disposition = NormalizeCallSignal(payload.Disposition);
        var dialStatus = NormalizeCallSignal(payload.DialStatus);
        var hangupCause = ParseNullableNonNegativeInt(payload.HangupCause);
        var status = ResolveCallStatus(provider, durationSeconds, disposition, dialStatus);

        var call = existing ?? new CrmCallEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            Provider = provider,
            ProviderAccountId = providerAccountId,
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
        call.Status = status;
        call.Disposition = disposition;
        call.DialStatus = dialStatus;
        call.HangupCause = hangupCause;
        call.RecordingUrl = recordingUrl ?? call.RecordingUrl;
        if (call.RecordingUrl is not null && call.RecordingStoragePath is null)
        {
            call.NextRecordingArchiveAtUtc ??= now;
        }
        if (provider == CrmTelephonyProviders.Plusofon)
        {
            call.NextRecordingFetchAtUtc = call.RecordingUrl is null ? now : null;
        }
        call.UpdatedAtUtc = now;
        if (existing is null)
        {
            db.CrmCalls.Add(call);
        }

        CrmDeskAlertEntity? missedCallAlert = null;
        string? missedCallRecipient = null;
        if (existing is null
            && card is not null
            && direction == CrmCallDirections.Incoming
            && CrmCallStatuses.IsUnanswered(status))
        {
            missedCallRecipient = !string.IsNullOrWhiteSpace(card.ManagerUserId)
                ? card.ManagerUserId
                : managerUserId;
            if (!string.IsNullOrWhiteSpace(missedCallRecipient))
            {
                var candidateName = await db.CandidateResponses.AsNoTracking()
                    .Where(x => x.Id == card.ResponseId)
                    .Select(x => x.FullName)
                    .FirstOrDefaultAsync(ct);
                missedCallAlert = new CrmDeskAlertEntity
                {
                    Id = Guid.NewGuid(),
                    OfficeId = officeId,
                    RecipientUserId = missedCallRecipient,
                    Kind = CrmTaskNotificationKinds.MissedCall,
                    CardId = card.Id,
                    Title = string.IsNullOrWhiteSpace(candidateName) ? "Кандидат" : candidateName,
                    Message = BuildUnansweredCallMessage(status, clientPhone),
                    CreatedAtUtc = now
                };
                db.CrmDeskAlerts.Add(missedCallAlert);
            }
        }

        if (providerAccountId is Guid statusAccountId)
        {
            var statusAccount = db.CrmTelephonyProviderAccounts.Local.FirstOrDefault(x => x.Id == statusAccountId)
                ?? await db.CrmTelephonyProviderAccounts.FirstOrDefaultAsync(x => x.Id == statusAccountId, ct);
            if (statusAccount is not null)
            {
                statusAccount.LastSyncedAtUtc = now;
                statusAccount.SyncStatus = "online";
                statusAccount.LastSyncError = null;
                statusAccount.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        if (call.CardId is Guid)
        {
            panelRealtime?.Notify([PanelChangeKind.Crm], officeId);
        }
        if (missedCallAlert is not null
            && missedCallRecipient is not null
            && crmNotificationRealtime is not null)
        {
            var notification = new CrmTaskNotificationDto(
                missedCallAlert.Id,
                Guid.Empty,
                missedCallAlert.CardId,
                missedCallAlert.Kind,
                missedCallAlert.Title,
                missedCallAlert.Message,
                missedCallAlert.CreatedAtUtc,
                missedCallAlert.CreatedAtUtc,
                null);
            try
            {
                await crmNotificationRealtime.NotifyAsync(missedCallRecipient, notification, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "Missed call alert {AlertId} was saved but realtime delivery to user {UserId} failed.",
                    missedCallAlert.Id,
                    missedCallRecipient);
            }
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
        && value.All(character => character <= 0x7f)
        && Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static bool IsValidSipUser(string value, int maximumLength) =>
        value.Length > 0
        && value.Length <= maximumLength
        && value.All(character => character is >= '!' and <= '~')
        && !ContainsControlCharacters(value);

    private static bool ContainsControlCharacters(string value) =>
        value.Any(char.IsControl) || value.IndexOfAny([';', '[', ']']) >= 0;

    private async Task<IReadOnlyList<CompetingDefaultUpdate>> ClearCompetingOfficeDefaultsAsync(
        Guid officeId,
        string selectedProvider,
        CancellationToken ct)
    {
        var providers = new[]
        {
            CrmTelephonyProviders.Sipout,
            CrmTelephonyProviders.Plusofon,
            CrmTelephonyProviders.Beeline
        };
        var updates = new List<CompetingDefaultUpdate>();
        foreach (var competingProvider in providers.Where(provider => provider != selectedProvider))
        {
            var receiver = await db.CrmTelephonyWebhooks.FirstOrDefaultAsync(x =>
                x.OfficeId == officeId && x.Provider == competingProvider, ct);
            if (receiver is null)
            {
                continue;
            }

            var accounts = ReadProviderAccounts(competingProvider, receiver).ToList();
            if (!accounts.Any(account => account.UseForOutbound))
            {
                continue;
            }
            accounts = accounts.Select(account => account with { UseForOutbound = false }).ToList();
            StoreProviderAccounts(competingProvider, receiver, accounts);
            receiver.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            updates.Add(new CompetingDefaultUpdate(competingProvider, accounts));
        }
        return updates;
    }

    private async Task<string?> ApplyCompetingDefaultUpdatesAsync(
        Guid officeId,
        IReadOnlyList<CompetingDefaultUpdate> updates,
        CancellationToken ct)
    {
        foreach (var update in updates)
        {
            var result = await WriteProviderAccountsAsync(update.Provider, officeId, update.Accounts, ct);
            if (!result.Success)
            {
                return result.Error;
            }
        }
        return null;
    }

    private Task<(bool Success, string? Error)> WriteProviderAccountsAsync(
        string provider,
        Guid officeId,
        IReadOnlyCollection<SipProviderCredentialPayload> accounts,
        CancellationToken ct)
    {
        var runtimeAccounts = accounts.Select(ToRuntimeAccount).ToList();
        return provider switch
        {
            CrmTelephonyProviders.Sipout => sipRuntimeConfigWriter!.WriteSipoutAccountsAsync(officeId, runtimeAccounts, ct),
            CrmTelephonyProviders.Plusofon => sipRuntimeConfigWriter!.WritePlusofonAccountsAsync(officeId, runtimeAccounts, ct),
            _ => sipRuntimeConfigWriter!.WriteBeelineAccountsAsync(officeId, runtimeAccounts, ct)
        };
    }

    private IReadOnlyList<SipProviderCredentialPayload> ReadProviderAccounts(
        string provider,
        CrmTelephonyWebhookEntity receiver) => provider switch
    {
        CrmTelephonyProviders.Sipout => ReadSipoutAccounts(receiver),
        CrmTelephonyProviders.Plusofon => ReadPlusofonAccounts(receiver),
        _ => ReadBeelineAccounts(receiver)
    };

    private void StoreProviderAccounts(
        string provider,
        CrmTelephonyWebhookEntity receiver,
        IReadOnlyCollection<SipProviderCredentialPayload> accounts)
    {
        if (provider == CrmTelephonyProviders.Beeline)
        {
            receiver.ProviderAccessTokenProtected = ProtectBeelineAccounts(accounts);
            receiver.ProviderClientId = accounts.FirstOrDefault(account => account.UseForOutbound)?.SipLogin
                ?? accounts.FirstOrDefault()?.SipLogin;
            return;
        }
        receiver.SipAccountProtected = provider == CrmTelephonyProviders.Sipout
            ? ProtectSipoutAccounts(accounts)
            : ProtectPlusofonAccounts(accounts);
    }

    private IReadOnlyList<SipProviderCredentialPayload> ReadBeelineAccounts(CrmTelephonyWebhookEntity receiver)
    {
        if (credentialProtector is null || string.IsNullOrWhiteSpace(receiver.ProviderAccessTokenProtected))
        {
            return [];
        }

        var json = credentialProtector.Unprotect(receiver.ProviderAccessTokenProtected);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Accounts", out _)
            || document.RootElement.TryGetProperty("accounts", out _))
        {
            var envelope = JsonSerializer.Deserialize<SipProviderCredentialEnvelope>(json);
            return envelope?.Accounts?
                .Where(account => NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) is not null)
                .Select(NormalizeStoredAccount)
                .ToList() ?? [];
        }

        // Compatibility with the single-account format already stored in production.
        var legacy = JsonSerializer.Deserialize<SipProviderCredentialPayload>(json);
        return legacy is null ? [] : [NormalizeStoredAccount(legacy with
        {
            AccountKey = "default",
            Name = string.IsNullOrWhiteSpace(legacy.Name) ? "Общая линия Билайна" : legacy.Name,
            Mode = CrmSipAccountModes.Shared
        })];
    }

    private string ProtectBeelineAccounts(IReadOnlyCollection<SipProviderCredentialPayload> accounts)
    {
        if (credentialProtector is null)
        {
            throw new InvalidOperationException("Telephony credential protection is unavailable.");
        }
        var normalized = accounts.Select(NormalizeStoredAccount).OrderBy(x => x.AccountKey, StringComparer.Ordinal).ToList();
        return credentialProtector.Protect(JsonSerializer.Serialize(new SipProviderCredentialEnvelope(2, normalized)));
    }

    private IReadOnlyList<SipProviderCredentialPayload> ReadPlusofonAccounts(CrmTelephonyWebhookEntity receiver)
    {
        if (credentialProtector is null || string.IsNullOrWhiteSpace(receiver.SipAccountProtected))
        {
            return [];
        }

        var json = credentialProtector.Unprotect(receiver.SipAccountProtected);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Accounts", out _)
            || document.RootElement.TryGetProperty("accounts", out _))
        {
            var envelope = JsonSerializer.Deserialize<SipProviderCredentialEnvelope>(json);
            return envelope?.Accounts?
                .Where(account => NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) is not null)
                .Select(NormalizeStoredPlusofonAccount)
                .ToList() ?? [];
        }

        // Upgrade the original single Plusofon line in place without changing
        // its endpoint name or the office's active outbound route.
        var legacy = JsonSerializer.Deserialize<SipProviderCredentialPayload>(json);
        return legacy is null ? [] : [NormalizeStoredPlusofonAccount(legacy with
        {
            AccountKey = "default",
            Name = string.IsNullOrWhiteSpace(legacy.Name) ? "Общая линия Плюсофона" : legacy.Name,
            Mode = CrmSipAccountModes.Shared
        })];
    }

    private string ProtectPlusofonAccounts(IReadOnlyCollection<SipProviderCredentialPayload> accounts)
    {
        if (credentialProtector is null)
        {
            throw new InvalidOperationException("Telephony credential protection is unavailable.");
        }
        var normalized = accounts
            .Select(NormalizeStoredPlusofonAccount)
            .OrderBy(x => x.AccountKey, StringComparer.Ordinal)
            .ToList();
        return credentialProtector.Protect(JsonSerializer.Serialize(new SipProviderCredentialEnvelope(2, normalized)));
    }

    private IReadOnlyList<SipProviderCredentialPayload> ReadSipoutAccounts(CrmTelephonyWebhookEntity receiver)
    {
        if (credentialProtector is null || string.IsNullOrWhiteSpace(receiver.SipAccountProtected))
        {
            return [];
        }

        var json = credentialProtector.Unprotect(receiver.SipAccountProtected);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Accounts", out _)
            || document.RootElement.TryGetProperty("accounts", out _))
        {
            var envelope = JsonSerializer.Deserialize<SipProviderCredentialEnvelope>(json);
            return envelope?.Accounts?
                .Where(account => NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) is not null)
                .Select(NormalizeStoredSipoutAccount)
                .ToList() ?? [];
        }

        var legacy = JsonSerializer.Deserialize<SipProviderCredentialPayload>(json);
        return legacy is null ? [] : [NormalizeStoredSipoutAccount(legacy with
        {
            AccountKey = "default",
            Name = string.IsNullOrWhiteSpace(legacy.Name) ? "Общая линия SIPOUT" : legacy.Name,
            Mode = CrmSipAccountModes.Shared
        })];
    }

    private string ProtectSipoutAccounts(IReadOnlyCollection<SipProviderCredentialPayload> accounts)
    {
        if (credentialProtector is null)
        {
            throw new InvalidOperationException("Telephony credential protection is unavailable.");
        }
        var normalized = accounts
            .Select(NormalizeStoredSipoutAccount)
            .OrderBy(x => x.AccountKey, StringComparer.Ordinal)
            .ToList();
        return credentialProtector.Protect(JsonSerializer.Serialize(new SipProviderCredentialEnvelope(2, normalized)));
    }

    private static SipProviderCredentialPayload NormalizeStoredAccount(SipProviderCredentialPayload account) => account with
    {
        AccountKey = NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) ?? "default",
        Name = string.IsNullOrWhiteSpace(account.Name) ? "Линия Билайна" : account.Name.Trim(),
        Mode = CrmSipAccountModes.IsSupported(account.Mode)
            ? CrmSipAccountModes.Normalize(account.Mode)
            : CrmSipAccountModes.Shared,
        Transport = account.Transport.Trim().ToLowerInvariant()
    };

    private static SipProviderCredentialPayload NormalizeStoredPlusofonAccount(SipProviderCredentialPayload account) => account with
    {
        AccountKey = NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) ?? "default",
        Name = string.IsNullOrWhiteSpace(account.Name) ? "Линия Плюсофона" : account.Name.Trim(),
        Mode = CrmSipAccountModes.IsSupported(account.Mode)
            ? CrmSipAccountModes.Normalize(account.Mode)
            : CrmSipAccountModes.Shared,
        Transport = account.Transport.Trim().ToLowerInvariant()
    };

    private static SipProviderCredentialPayload NormalizeStoredSipoutAccount(SipProviderCredentialPayload account) => account with
    {
        AccountKey = NormalizeSipAccountKey(account.AccountKey, generateWhenEmpty: false) ?? "default",
        Name = string.IsNullOrWhiteSpace(account.Name) ? "Линия SIPOUT" : account.Name.Trim(),
        Mode = CrmSipAccountModes.IsSupported(account.Mode)
            ? CrmSipAccountModes.Normalize(account.Mode)
            : CrmSipAccountModes.Shared,
        Transport = account.Transport.Trim().ToLowerInvariant(),
        InternalNumber = new string((account.InternalNumber ?? string.Empty).Where(char.IsDigit).ToArray())
    };

    private static string? ResolveSipoutAccountKey(
        string? outboundProvider,
        IReadOnlyList<SipProviderCredentialPayload> accounts)
    {
        if (CrmTelephonyOutboundProviders.TryGetSipoutLineKey(outboundProvider, out var accountKey))
        {
            return accounts.Any(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                ? accountKey
                : null;
        }
        if (string.Equals(outboundProvider, CrmTelephonyProviders.Sipout, StringComparison.OrdinalIgnoreCase))
        {
            return accounts.FirstOrDefault(x => x.UseForOutbound)?.AccountKey
                ?? accounts.FirstOrDefault(x => x.AccountKey == "default")?.AccountKey;
        }
        return null;
    }

    private static string? ResolveBeelineAccountKey(
        string? outboundProvider,
        IReadOnlyList<SipProviderCredentialPayload> accounts)
    {
        if (CrmTelephonyOutboundProviders.TryGetBeelineLineKey(outboundProvider, out var accountKey))
        {
            return accounts.Any(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                ? accountKey
                : null;
        }
        if (string.Equals(outboundProvider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase))
        {
            return accounts.FirstOrDefault(x => x.UseForOutbound)?.AccountKey
                ?? accounts.FirstOrDefault(x => x.AccountKey == "default")?.AccountKey;
        }
        return null;
    }

    private static string? ResolvePlusofonAccountKey(
        string? outboundProvider,
        IReadOnlyList<SipProviderCredentialPayload> accounts)
    {
        if (CrmTelephonyOutboundProviders.TryGetPlusofonLineKey(outboundProvider, out var accountKey))
        {
            return accounts.Any(x => string.Equals(x.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                ? accountKey
                : null;
        }
        if (string.Equals(outboundProvider, CrmTelephonyProviders.Plusofon, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outboundProvider, CrmTelephonyOutboundProviders.Default, StringComparison.OrdinalIgnoreCase))
        {
            return accounts.FirstOrDefault(x => x.UseForOutbound)?.AccountKey
                ?? accounts.FirstOrDefault(x => x.AccountKey == "default")?.AccountKey;
        }
        return null;
    }

    private static CrmSipRuntimeAccount ToRuntimeAccount(SipProviderCredentialPayload account) => new(
        account.Server,
        account.Domain,
        account.Port,
        account.Transport,
        account.SipLogin,
        account.AuthorizationLogin,
        account.Password,
        account.UseForOutbound,
        account.AccountKey,
        account.Name,
        account.Mode,
        account.OutboundCallerId ?? string.Empty,
        account.InternalNumber ?? string.Empty);

    private static string? NormalizeSipAccountKey(string? value, bool generateWhenEmpty = true)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return generateWhenEmpty ? Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() : null;
        }
        return candidate.Length <= 16
            && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? candidate
                : null;
    }

    private (CrmAsteriskWebRtcEndpoint Endpoint, bool Changed) EnsureWebRtcCredentials(
        CrmTelephonyUserBindingEntity binding)
    {
        if (credentialProtector is null)
        {
            throw new InvalidOperationException("Telephony credential protection is unavailable.");
        }

        var extension = binding.ProviderUserKey.Trim();
        var expectedAuthorizationUsername = $"{extension}-webrtc";
        if (string.Equals(
                binding.WebRtcAuthorizationUsername,
                expectedAuthorizationUsername,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(binding.WebRtcPasswordProtected))
        {
            try
            {
                var existingPassword = credentialProtector.Unprotect(binding.WebRtcPasswordProtected);
                if (existingPassword.Length is >= 16 and <= 512)
                {
                    return (new CrmAsteriskWebRtcEndpoint(
                        extension,
                        expectedAuthorizationUsername,
                        existingPassword), false);
                }
            }
            catch (CryptographicException)
            {
                // The Data Protection key may have changed. Replace only this
                // browser credential and publish the new endpoint to Asterisk.
            }
        }

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        binding.WebRtcAuthorizationUsername = expectedAuthorizationUsername;
        binding.WebRtcPasswordProtected = credentialProtector.Protect(password);
        return (new CrmAsteriskWebRtcEndpoint(
            extension,
            expectedAuthorizationUsername,
            password), true);
    }

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
        bool UseForOutbound,
        string AccountKey = "default",
        string Name = "Общая линия Билайна",
        string Mode = CrmSipAccountModes.Shared,
        string OutboundCallerId = "",
        string InternalNumber = "");

    private sealed record SipProviderCredentialEnvelope(
        int Version,
        IReadOnlyList<SipProviderCredentialPayload> Accounts);

    private sealed record CompetingDefaultUpdate(
        string Provider,
        IReadOnlyList<SipProviderCredentialPayload> Accounts);

    private sealed record CloudAccountBindingRow(
        Guid AccountId,
        CrmTelephonyUserBindingDto Binding);

    private static string NormalizeDirection(
        string? callType,
        string? callerPhone,
        string? calledPhone,
        string? providerUserKey)
    {
        var value = callType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value is "external" or "outbound" or "outgoing"
            || value.Contains("исход", StringComparison.Ordinal))
        {
            return CrmCallDirections.Outgoing;
        }
        if (value is "internal" or "inbound" or "incoming"
            || value.Contains("вход", StringComparison.Ordinal))
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

    private static int? ParseNullableNonNegativeInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : null;

    private static string? NormalizeCallSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .Take(32)
            .ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static string ResolveCallStatus(
        string provider,
        int durationSeconds,
        string? disposition,
        string? dialStatus)
    {
        if (provider != CrmTelephonyProviders.Asterisk)
        {
            return durationSeconds > 0 ? CrmCallStatuses.Answered : CrmCallStatuses.Unknown;
        }

        if (dialStatus == "ANSWER")
        {
            return CrmCallStatuses.Answered;
        }
        if (dialStatus == "BUSY")
        {
            return CrmCallStatuses.Rejected;
        }
        if (dialStatus is "NOANSWER" or "CANCEL")
        {
            return CrmCallStatuses.Missed;
        }
        if (dialStatus is "CHANUNAVAIL" or "CONGESTION" or "DONTCALL" or "TORTURE" or "INVALIDARGS")
        {
            return CrmCallStatuses.Failed;
        }
        if (disposition == "ANSWERED")
        {
            return CrmCallStatuses.Answered;
        }
        if (disposition == "BUSY")
        {
            return CrmCallStatuses.Rejected;
        }
        if (disposition == "NOANSWER")
        {
            return CrmCallStatuses.Missed;
        }
        if (disposition == "FAILED")
        {
            return CrmCallStatuses.Failed;
        }

        return durationSeconds > 0 ? CrmCallStatuses.Answered : CrmCallStatuses.Unknown;
    }

    private static string BuildUnansweredCallMessage(string status, string clientPhone)
    {
        var phone = string.IsNullOrWhiteSpace(clientPhone) ? string.Empty : $" от +{clientPhone}";
        return status switch
        {
            CrmCallStatuses.Rejected => $"Входящий звонок{phone} был отклонён.",
            CrmCallStatuses.Failed => $"Входящий звонок{phone} не удалось доставить.",
            _ => $"Пропущен входящий звонок{phone}."
        };
    }

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
