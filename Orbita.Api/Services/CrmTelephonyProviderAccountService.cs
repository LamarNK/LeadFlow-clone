using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CrmTelephonyProviderAccountService(
    OrbitaDbContext db,
    PhoneNormalizer phoneNormalizer,
    CrmTelephonyCredentialProtector credentialProtector,
    TimeProvider timeProvider)
{
    private const int MaximumInitialHistoryDays = 180;

    public async Task<IReadOnlyList<CrmTelephonyProviderAccountDto>> GetAsync(
        Guid officeId,
        string provider,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var accounts = await db.CrmTelephonyProviderAccounts.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Provider == provider)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        var ids = accounts.Select(x => x.Id).ToList();
        var bindings = ids.Count == 0
            ? new List<AccountBindingRow>()
            : await (
                from binding in db.CrmTelephonyProviderAccountBindings.AsNoTracking()
                join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
                join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
                where ids.Contains(binding.ProviderAccountId)
                select new AccountBindingRow(
                    binding.ProviderAccountId,
                    new CrmTelephonyUserBindingDto(
                        binding.UserId,
                        string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                        binding.ProviderUserKey)))
                .ToListAsync(ct);
        return accounts.Select(x =>
        {
            var accountBindings = bindings.Where(binding => binding.AccountId == x.Id)
                .Select(binding => binding.Binding)
                .OrderBy(binding => binding.UserName)
                .ToList();
            return ToDto(x, accountBindings.Count, accountBindings);
        }).ToList();
    }

    public async Task<(CrmTelephonyProviderAccountReceiverDto? Result, string? Error)> CreateAsync(
        Guid officeId,
        string provider,
        CreateCrmTelephonyProviderAccountRequest request,
        string publicBaseUrl,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var validation = Validate(provider, request.Name, request.ExternalAccountId, request.AccessToken, requireToken: true);
        if (validation is not null) return (null, validation);
        if (!await db.Offices.AsNoTracking().AnyAsync(x => x.Id == officeId, ct))
        {
            return (null, "Офис не найден.");
        }

        var name = request.Name.Trim();
        if (await db.CrmTelephonyProviderAccounts.AsNoTracking().AnyAsync(x =>
            x.OfficeId == officeId && x.Provider == provider && x.Name == name, ct))
        {
            return (null, "Кабинет с таким названием уже добавлен в этот офис.");
        }
        var externalAccountId = NormalizeNullable(request.ExternalAccountId);
        if (externalAccountId is not null
            && await db.CrmTelephonyProviderAccounts.AsNoTracking().AnyAsync(x =>
                x.OfficeId == officeId
                && x.Provider == provider
                && x.ExternalAccountId == externalAccountId, ct))
        {
            return (null, "Этот кабинет провайдера уже подключён к офису.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var secret = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var entity = new CrmTelephonyProviderAccountEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            Provider = provider,
            Name = name,
            ExternalAccountId = externalAccountId,
            AccessTokenProtected = string.IsNullOrWhiteSpace(request.AccessToken)
                ? null
                : credentialProtector.Protect(request.AccessToken.Trim()),
            OwnedNumbersJson = SerializeOwnedNumbers(request.OwnedNumbers),
            PublicId = Guid.NewGuid(),
            SecretHash = HashSecret(secret),
            IsEnabled = true,
            SyncFromUtc = NormalizeSyncFrom(request.SyncFromUtc, now),
            SyncStatus = provider == CrmTelephonyProviders.Plusofon ? "pending" : "webhook",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CrmTelephonyProviderAccounts.Add(entity);
        await db.SaveChangesAsync(ct);
        return (BuildReceiver(entity, publicBaseUrl, secret), null);
    }

    public async Task<(CrmTelephonyProviderAccountDto? Account, string? Error)> UpdateAsync(
        Guid officeId,
        string provider,
        Guid accountId,
        UpdateCrmTelephonyProviderAccountRequest request,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var validation = Validate(provider, request.Name, request.ExternalAccountId, request.AccessToken, requireToken: false);
        if (validation is not null) return (null, validation);
        var entity = await db.CrmTelephonyProviderAccounts.FirstOrDefaultAsync(x =>
            x.Id == accountId && x.OfficeId == officeId && x.Provider == provider, ct);
        if (entity is null) return (null, "Кабинет телефонии не найден.");

        var name = request.Name.Trim();
        if (await db.CrmTelephonyProviderAccounts.AsNoTracking().AnyAsync(x =>
            x.Id != accountId && x.OfficeId == officeId && x.Provider == provider && x.Name == name, ct))
        {
            return (null, "Кабинет с таким названием уже добавлен в этот офис.");
        }
        var externalAccountId = NormalizeNullable(request.ExternalAccountId);
        if (externalAccountId is not null
            && await db.CrmTelephonyProviderAccounts.AsNoTracking().AnyAsync(x =>
                x.Id != accountId
                && x.OfficeId == officeId
                && x.Provider == provider
                && x.ExternalAccountId == externalAccountId, ct))
        {
            return (null, "Этот кабинет провайдера уже подключён к офису.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        entity.Name = name;
        entity.ExternalAccountId = externalAccountId;
        if (!string.IsNullOrWhiteSpace(request.AccessToken))
        {
            entity.AccessTokenProtected = credentialProtector.Protect(request.AccessToken.Trim());
        }
        entity.OwnedNumbersJson = SerializeOwnedNumbers(request.OwnedNumbers);
        entity.IsEnabled = request.IsEnabled;
        if (request.SyncFromUtc is DateTime requestedFrom)
        {
            entity.SyncFromUtc = NormalizeSyncFrom(requestedFrom, now);
            entity.SyncCursorUtc = null;
            entity.SyncStatus = provider == CrmTelephonyProviders.Plusofon ? "pending" : "webhook";
            entity.LastSyncError = null;
        }
        entity.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        var boundUsers = await db.CrmTelephonyProviderAccountBindings.AsNoTracking()
            .CountAsync(x => x.ProviderAccountId == entity.Id, ct);
        return (ToDto(entity, boundUsers), null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        Guid officeId,
        string provider,
        Guid accountId,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var entity = await db.CrmTelephonyProviderAccounts.FirstOrDefaultAsync(x =>
            x.Id == accountId && x.OfficeId == officeId && x.Provider == provider, ct);
        if (entity is null) return (false, "Кабинет телефонии не найден.");

        // Calls and archived recordings are CRM history and must survive cabinet removal.
        // Detach them from the account and replace the provider-side key so the partial
        // unique index for legacy/accountless calls cannot collide across old cabinets.
        var calls = await db.CrmCalls
            .Where(x => x.ProviderAccountId == accountId)
            .ToListAsync(ct);
        foreach (var call in calls)
        {
            call.ProviderAccountId = null;
            call.ExternalCallId = BuildRemovedAccountCallId(accountId, call.ExternalCallId);
        }

        var bindings = await db.CrmTelephonyProviderAccountBindings
            .Where(x => x.ProviderAccountId == accountId)
            .ToListAsync(ct);
        db.CrmTelephonyProviderAccountBindings.RemoveRange(bindings);
        db.CrmTelephonyProviderAccounts.Remove(entity);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(CrmTelephonyProviderAccountReceiverDto? Result, string? Error)> RotateReceiverAsync(
        Guid officeId,
        string provider,
        Guid accountId,
        string publicBaseUrl,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var entity = await db.CrmTelephonyProviderAccounts.FirstOrDefaultAsync(x =>
            x.Id == accountId && x.OfficeId == officeId && x.Provider == provider, ct);
        if (entity is null) return (null, "Кабинет телефонии не найден.");
        var secret = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        entity.PublicId = Guid.NewGuid();
        entity.SecretHash = HashSecret(secret);
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var count = await db.CrmTelephonyProviderAccountBindings.AsNoTracking()
            .CountAsync(x => x.ProviderAccountId == entity.Id, ct);
        return (BuildReceiver(entity, publicBaseUrl, secret, count), null);
    }

    public async Task<(bool Success, string? Error)> SetBindingAsync(
        Guid officeId,
        string provider,
        Guid accountId,
        UpdateCrmTelephonyProviderAccountBindingRequest request,
        CancellationToken ct = default)
    {
        provider = NormalizeProvider(provider);
        var account = await db.CrmTelephonyProviderAccounts.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == accountId && x.OfficeId == officeId && x.Provider == provider, ct);
        if (account is null) return (false, "Кабинет телефонии не найден.");
        var key = request.ProviderUserKey.Trim().ToLowerInvariant();
        if (key.Length is 0 or > 128) return (false, "Укажите внутренний номер или SIP-аккаунт сотрудника.");
        if (!await db.PanelUserProfiles.AsNoTracking().AnyAsync(x => x.UserId == request.UserId && x.OfficeId == officeId, ct))
        {
            return (false, "Сотрудник этого офиса не найден.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var byUser = await db.CrmTelephonyProviderAccountBindings.FirstOrDefaultAsync(x =>
            x.ProviderAccountId == accountId && x.UserId == request.UserId, ct);
        var conflicting = await db.CrmTelephonyProviderAccountBindings.FirstOrDefaultAsync(x =>
            x.ProviderAccountId == accountId && x.ProviderUserKey == key && x.UserId != request.UserId, ct);
        if (conflicting is not null) db.CrmTelephonyProviderAccountBindings.Remove(conflicting);
        if (byUser is null)
        {
            db.CrmTelephonyProviderAccountBindings.Add(new CrmTelephonyProviderAccountBindingEntity
            {
                Id = Guid.NewGuid(),
                ProviderAccountId = accountId,
                UserId = request.UserId,
                ProviderUserKey = key,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        else
        {
            byUser.ProviderUserKey = key;
            byUser.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    internal static CrmTelephonyProviderAccountDto ToDto(
        CrmTelephonyProviderAccountEntity entity,
        int boundUsers = 0,
        IReadOnlyList<CrmTelephonyUserBindingDto>? bindings = null) => new(
        entity.Id,
        entity.OfficeId,
        entity.Provider,
        entity.Name,
        entity.ExternalAccountId,
        DeserializeOwnedNumbers(entity.OwnedNumbersJson),
        !string.IsNullOrWhiteSpace(entity.ExternalAccountId) && !string.IsNullOrWhiteSpace(entity.AccessTokenProtected),
        entity.IsEnabled,
        entity.PublicId,
        entity.SyncFromUtc,
        entity.SyncCursorUtc,
        entity.LastSyncedAtUtc,
        entity.SyncStatus,
        entity.LastSyncError,
        boundUsers,
        bindings);

    internal static IReadOnlyList<string> DeserializeOwnedNumbers(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private CrmTelephonyProviderAccountReceiverDto BuildReceiver(
        CrmTelephonyProviderAccountEntity entity,
        string publicBaseUrl,
        string secret,
        int boundUsers = 0)
    {
        var callback = $"{publicBaseUrl.TrimEnd('/')}/api/v1/integrations/telephony/{entity.Provider}/{entity.PublicId:D}";
        if (entity.Provider == CrmTelephonyProviders.Sipout)
        {
            callback += $"?secret={Uri.EscapeDataString(secret)}"
                + "&CID=%VAR:CID%&DID=%VAR:DID%&C_ID=%VAR:C_ID%&C_TYPE=%VAR:C_TYPE%"
                + "&C_START=%VAR:C_START%&C_TIME=%VAR:C_TIME%&PREV_EXTEN=%VAR:PREV_EXTEN%"
                + "&LAST_CALLER=%VAR:LAST_CALLER%&LAST_RECORDING_URL=%VAR:LAST_RECORDING_URL%";
        }
        else
        {
            // Plusofon installations do not all support custom webhook headers.
            // Keep the signed, one-time URL pasteable directly from the UI; the
            // header remains available for accounts that can send one.
            callback += $"?secret={Uri.EscapeDataString(secret)}";
        }
        return new CrmTelephonyProviderAccountReceiverDto(
            ToDto(entity, boundUsers),
            callback,
            secret,
            "X-Orbita-Webhook-Secret");
    }

    private string SerializeOwnedNumbers(IReadOnlyList<string>? numbers)
    {
        var normalized = (numbers ?? [])
            .Select(phoneNormalizer.Normalize)
            .Where(x => x.Length >= 10)
            .Distinct(StringComparer.Ordinal)
            .Take(500)
            .ToList();
        return JsonSerializer.Serialize(normalized);
    }

    private static string? Validate(
        string provider,
        string? name,
        string? externalAccountId,
        string? accessToken,
        bool requireToken)
    {
        if (provider is not (CrmTelephonyProviders.Plusofon or CrmTelephonyProviders.Sipout))
        {
            return "Кабинеты поддерживаются для Плюсофона и SIPOUT.";
        }
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
        {
            return "Укажите название кабинета длиной до 128 символов.";
        }
        if (provider == CrmTelephonyProviders.Plusofon
            && (string.IsNullOrWhiteSpace(externalAccountId) || externalAccountId.Trim().Length > 128))
        {
            return "Укажите Client ID кабинета Плюсофона.";
        }
        if (provider == CrmTelephonyProviders.Plusofon
            && requireToken
            && (string.IsNullOrWhiteSpace(accessToken) || accessToken.Trim().Length > 4096))
        {
            return "Укажите Access Token кабинета Плюсофона.";
        }
        if (!string.IsNullOrWhiteSpace(accessToken) && accessToken.Trim().Length > 4096)
        {
            return "Access Token слишком длинный.";
        }
        return null;
    }

    private static string NormalizeProvider(string provider) =>
        CrmTelephonyProviders.Normalize(provider);

    private static string BuildRemovedAccountCallId(Guid accountId, string externalCallId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalCallId)))
            .ToLowerInvariant();
        return $"removed:{accountId:N}:{hash[..24]}";
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeSyncFrom(DateTime? value, DateTime nowUtc)
    {
        var minimum = nowUtc.AddDays(-MaximumInitialHistoryDays);
        var candidate = value?.ToUniversalTime() ?? nowUtc.AddDays(-7);
        return candidate < minimum ? minimum : candidate > nowUtc ? nowUtc : candidate;
    }

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AccountBindingRow(Guid AccountId, CrmTelephonyUserBindingDto Binding);
}
