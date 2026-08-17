using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CrmTelephonyService(
    OrbitaDbContext db,
    PhoneNormalizer phoneNormalizer,
    TimeProvider timeProvider,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    private const string Provider = CrmTelephonyProviders.Sipout;

    public async Task<CrmTelephonySettingsDto?> GetSettingsAsync(Guid officeId, CancellationToken ct = default)
    {
        if (!await db.Offices.AsNoTracking().AnyAsync(x => x.Id == officeId, ct))
        {
            return null;
        }

        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == Provider, ct);
        var bindings = await (
            from binding in db.CrmTelephonyUserBindings.AsNoTracking()
            join profile in db.PanelUserProfiles.AsNoTracking() on binding.UserId equals profile.UserId
            join user in db.Users.AsNoTracking() on binding.UserId equals user.Id
            where binding.OfficeId == officeId && binding.Provider == Provider
            orderby profile.FullName, user.Email
            select new CrmTelephonyUserBindingDto(
                binding.UserId,
                string.IsNullOrWhiteSpace(profile.FullName) ? user.Email ?? binding.UserId : profile.FullName,
                binding.ProviderUserKey))
            .ToListAsync(ct);

        return new CrmTelephonySettingsDto(
            officeId,
            Provider,
            receiver is not null,
            receiver?.IsEnabled == true,
            receiver?.PublicId,
            bindings);
    }

    public async Task<(CrmTelephonyReceiverDto? Receiver, string? Error)> RotateReceiverAsync(
        Guid officeId,
        string publicBaseUrl,
        CancellationToken ct = default)
    {
        if (!await db.Offices.AnyAsync(x => x.Id == officeId, ct))
        {
            return (null, "Офис не найден.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var publicId = Guid.NewGuid();
        var secret = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == Provider, ct);
        if (receiver is null)
        {
            receiver = new CrmTelephonyWebhookEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = Provider,
                CreatedAtUtc = now
            };
            db.CrmTelephonyWebhooks.Add(receiver);
        }

        receiver.PublicId = publicId;
        receiver.SecretHash = HashSecret(secret);
        receiver.IsEnabled = true;
        receiver.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        var callbackUrl = $"{publicBaseUrl.TrimEnd('/')}/api/v1/integrations/telephony/sipout/{publicId:D}";
        // SIPOUT replaces %VAR:...% placeholders inside a WebRequest URL.
        // Keep these placeholders unescaped; escaping '%' would stop provider-side substitution.
        var sipoutUrl = $"{callbackUrl}?secret={Uri.EscapeDataString(secret)}"
            + "&CID=%VAR:CID%&DID=%VAR:DID%&C_ID=%VAR:C_ID%&C_TYPE=%VAR:C_TYPE%"
            + "&C_START=%VAR:C_START%&C_TIME=%VAR:C_TIME%&PREV_EXTEN=%VAR:PREV_EXTEN%"
            + "&LAST_CALLER=%VAR:LAST_CALLER%&LAST_RECORDING_URL=%VAR:LAST_RECORDING_URL%";
        return (new CrmTelephonyReceiverDto(officeId, Provider, publicId, callbackUrl, sipoutUrl), null);
    }

    public async Task<bool> SetEnabledAsync(Guid officeId, bool enabled, CancellationToken ct = default)
    {
        var receiver = await db.CrmTelephonyWebhooks
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == Provider, ct);
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
        CancellationToken ct = default)
    {
        var normalizedKey = NormalizeProviderUserKey(providerUserKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return (null, "Укажите внутренний номер или логин SIPOUT.");
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

        var duplicate = await db.CrmTelephonyUserBindings.AsNoTracking()
            .AnyAsync(x => x.OfficeId == officeId
                && x.Provider == Provider
                && x.ProviderUserKey == normalizedKey
                && x.UserId != userId, ct);
        if (duplicate)
        {
            return (null, "Этот SIPOUT-аккаунт уже привязан к другому сотруднику.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var binding = await db.CrmTelephonyUserBindings
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == Provider && x.UserId == userId, ct);
        if (binding is null)
        {
            binding = new CrmTelephonyUserBindingEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Provider = Provider,
                UserId = userId,
                CreatedAtUtc = now
            };
            db.CrmTelephonyUserBindings.Add(binding);
        }

        binding.ProviderUserKey = normalizedKey;
        binding.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        var userName = string.IsNullOrWhiteSpace(user.Profile.FullName)
            ? user.Email ?? userId
            : user.Profile.FullName;
        return (new CrmTelephonyUserBindingDto(userId, userName, normalizedKey), null);
    }

    public async Task<bool> RemoveBindingAsync(Guid officeId, string userId, CancellationToken ct = default)
    {
        var binding = await db.CrmTelephonyUserBindings
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.Provider == Provider && x.UserId == userId, ct);
        if (binding is null)
        {
            return false;
        }

        db.CrmTelephonyUserBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SipoutCallReceiveResult> ReceiveSipoutCallAsync(
        Guid publicId,
        string? secret,
        SipoutCallWebhookPayload payload,
        CancellationToken ct = default)
    {
        var receiver = await db.CrmTelephonyWebhooks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId && x.Provider == Provider && x.IsEnabled, ct);
        if (receiver is null || !VerifySecret(secret, receiver.SecretHash))
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Unauthorized);
        }

        var externalCallId = payload.ExternalCallId.Trim();
        if (string.IsNullOrWhiteSpace(externalCallId) || externalCallId.Length > 128)
        {
            return new SipoutCallReceiveResult(SipoutCallReceiveOutcome.Invalid, Message: "C_ID is required.");
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
                && x.Provider == Provider
                && x.ExternalCallId == externalCallId, ct);
        var card = await FindCardAsync(receiver.OfficeId, candidatePhones, ct);
        var providerUserKey = ResolveProviderUserKey(payload);
        var managerUserId = string.IsNullOrWhiteSpace(providerUserKey)
            ? null
            : await db.CrmTelephonyUserBindings.AsNoTracking()
                .Where(x => x.OfficeId == receiver.OfficeId
                    && x.Provider == Provider
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
            Provider = Provider,
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

    private static bool IsInternalPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 8;
    }

    private static bool IsExternalPhone(string normalized) => normalized.Length >= 10;

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
