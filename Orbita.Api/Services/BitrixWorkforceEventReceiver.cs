using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed record BitrixWorkforceIncomingEvent(
    string EventName,
    string EventHandlerId,
    long DealId,
    long Timestamp,
    string Domain,
    string MemberId,
    string ApplicationToken);

public enum BitrixWorkforceReceiveOutcome
{
    Accepted,
    Duplicate,
    Ignored,
    Unauthorized,
    Invalid
}

public sealed record BitrixWorkforceReceiveResult(
    BitrixWorkforceReceiveOutcome Outcome,
    string Message);

public sealed class BitrixWorkforceEventReceiver(OrbitaDbContext db)
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ONCRMDEALADD",
        "ONCRMDEALUPDATE"
    };

    public async Task<BitrixWorkforceReceiveResult> ReceiveAsync(
        Guid publicId,
        BitrixWorkforceIncomingEvent incoming,
        CancellationToken ct = default)
    {
        var credential = await db.BitrixWorkforceEventCredentials
            .Include(x => x.BitrixInstance)
            .FirstOrDefaultAsync(x => x.PublicId == publicId, ct);
        if (credential is null)
        {
            return new(BitrixWorkforceReceiveOutcome.Unauthorized, "Unknown receiver.");
        }

        if (!TokenMatches(incoming.ApplicationToken, credential.ApplicationTokenHash)
            || !DomainMatches(incoming.Domain, credential.BitrixInstance.PortalHost)
            || !MemberMatches(incoming.MemberId, credential.ExpectedMemberId))
        {
            return new(BitrixWorkforceReceiveOutcome.Unauthorized, "Webhook credentials do not match.");
        }

        if (!AllowedEvents.Contains(incoming.EventName) || incoming.DealId <= 0)
        {
            return new(BitrixWorkforceReceiveOutcome.Invalid, "Unsupported Bitrix24 event.");
        }

        var configuration = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == credential.BitrixInstanceId, ct);
        var now = DateTime.UtcNow;
        credential.LastAcceptedAtUtc = now;
        if (configuration is null
            || configuration.OperationMode == BitrixWorkforceDistribution.DisabledMode
            || !credential.BitrixInstance.IsEnabled)
        {
            await db.SaveChangesAsync(ct);
            return new(BitrixWorkforceReceiveOutcome.Ignored, "Workforce distribution is disabled.");
        }

        var eventKey = BuildEventKey(incoming);
        var duplicate = await db.BitrixDealEventInbox
            .AnyAsync(
                x => x.BitrixInstanceId == credential.BitrixInstanceId
                     && x.EventKey == eventKey,
                ct);
        if (duplicate)
        {
            await db.SaveChangesAsync(ct);
            return new(BitrixWorkforceReceiveOutcome.Duplicate, "Event was already accepted.");
        }

        var inbox = new BitrixDealEventInboxEntity
        {
            BitrixInstanceId = credential.BitrixInstanceId,
            EventName = incoming.EventName.ToUpperInvariant(),
            DealId = incoming.DealId,
            EventKey = eventKey,
            ReceivedAtUtc = now,
            State = BitrixWorkforceInboxStates.Pending,
            NextAttemptAtUtc = now
        };
        db.BitrixDealEventInbox.Add(inbox);

        try
        {
            await db.SaveChangesAsync(ct);
            return new(BitrixWorkforceReceiveOutcome.Accepted, "Event accepted.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(inbox).State = EntityState.Detached;
            return new(BitrixWorkforceReceiveOutcome.Duplicate, "Event was already accepted.");
        }
    }

    internal static string BuildEventKey(BitrixWorkforceIncomingEvent incoming)
    {
        var canonical = string.Join(
            '|',
            incoming.EventName.Trim().ToUpperInvariant(),
            incoming.EventHandlerId.Trim(),
            incoming.DealId.ToString(CultureInfo.InvariantCulture),
            incoming.Timestamp.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool TokenMatches(string token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return actual.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool DomainMatches(string actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var normalized = actual.Trim();
        if (Uri.TryCreate(
                normalized.Contains("://", StringComparison.Ordinal) ? normalized : $"https://{normalized}",
                UriKind.Absolute,
                out var uri))
        {
            normalized = uri.Host;
        }

        return string.Equals(normalized, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MemberMatches(string actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected)
        || string.Equals(actual.Trim(), expected.Trim(), StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };
}

public static class BitrixWorkforceEventParser
{
    public static async Task<BitrixWorkforceIncomingEvent?> ParseAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            return Build(
                form["event"],
                form["event_handler_id"],
                form["data[FIELDS][ID]"],
                form["ts"],
                form["auth[domain]"],
                form["auth[member_id]"],
                form["auth[application_token]"]);
        }

        if (!request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return null;
        }

        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var root = json.RootElement;
            var data = GetObject(root, "data");
            var fields = data is null ? null : GetObject(data.Value, "FIELDS");
            var auth = GetObject(root, "auth");
            return Build(
                GetString(root, "event"),
                GetString(root, "event_handler_id"),
                fields is null ? null : GetString(fields.Value, "ID"),
                GetString(root, "ts"),
                auth is null ? null : GetString(auth.Value, "domain"),
                auth is null ? null : GetString(auth.Value, "member_id"),
                auth is null ? null : GetString(auth.Value, "application_token"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BitrixWorkforceIncomingEvent? Build(
        StringValues eventName,
        StringValues eventHandlerId,
        StringValues dealId,
        StringValues timestamp,
        StringValues domain,
        StringValues memberId,
        StringValues applicationToken) =>
        Build(
            eventName.ToString(),
            eventHandlerId.ToString(),
            dealId.ToString(),
            timestamp.ToString(),
            domain.ToString(),
            memberId.ToString(),
            applicationToken.ToString());

    private static BitrixWorkforceIncomingEvent? Build(
        string? eventName,
        string? eventHandlerId,
        string? dealId,
        string? timestamp,
        string? domain,
        string? memberId,
        string? applicationToken)
    {
        if (string.IsNullOrWhiteSpace(eventName)
            || !long.TryParse(dealId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDealId)
            || parsedDealId <= 0
            || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimestamp))
        {
            return null;
        }

        return new BitrixWorkforceIncomingEvent(
            eventName.Trim(),
            eventHandlerId?.Trim() ?? string.Empty,
            parsedDealId,
            parsedTimestamp,
            domain?.Trim() ?? string.Empty,
            memberId?.Trim() ?? string.Empty,
            applicationToken?.Trim() ?? string.Empty);
    }

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
