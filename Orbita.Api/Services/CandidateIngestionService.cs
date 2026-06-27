using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateIngestionService(
    OrbitaDbContext db,
    UserManager<IdentityUser> users,
    WebhookSecretProtector protector,
    PhoneNormalizer phoneNormalizer,
    CandidateParser candidateParser,
    BitrixClient bitrixClient,
    IOptions<OrbitaBitrixSettings> bitrixOptions)
{
    public async Task<WorkerCandidateIngestionResultDto> IngestBatchAsync(
        Guid workerId,
        WorkerCandidateBatchRequest request,
        CancellationToken ct = default)
    {
        var workerExists = await db.Workers.AnyAsync(x => x.Id == workerId, ct);
        if (!workerExists)
        {
            return new WorkerCandidateIngestionResultDto(0, 0, 0, 0);
        }

        var webhookUrl = await ResolveAdminWebhookUrlAsync(ct);
        var received = request.Candidates.Count;
        var ingested = 0;
        var skippedDuplicates = 0;
        var errors = 0;

        foreach (var candidate in request.Candidates)
        {
            var phoneNormalized = phoneNormalizer.Normalize(candidate.PhoneRaw);
            if (string.IsNullOrWhiteSpace(candidate.SourceResponseId) || string.IsNullOrWhiteSpace(phoneNormalized))
            {
                errors++;
                continue;
            }

            var isDuplicate = await db.CandidateResponses.AnyAsync(
                x => x.AccountId == candidate.AccountId
                     && x.SourceResponseId == candidate.SourceResponseId
                     && x.PhoneNormalized == phoneNormalized,
                ct);
            if (isDuplicate)
            {
                skippedDuplicates++;
                continue;
            }

            var (firstName, lastName, middleName) = candidateParser.ParseName(candidate.FullName);
            var lead = new CandidateLead
            {
                AccountId = candidate.AccountId,
                AccountName = candidate.AccountName,
                Source = string.IsNullOrWhiteSpace(candidate.Source) ? "Avito" : candidate.Source,
                SourceResponseId = candidate.SourceResponseId,
                FullName = candidate.FullName,
                FirstName = firstName,
                LastName = lastName,
                MiddleName = middleName,
                Age = candidate.Age,
                PhoneRaw = candidate.PhoneRaw,
                PhoneNormalized = phoneNormalized,
                City = candidate.City,
                Vacancy = candidate.Vacancy,
                VacancyUrl = candidate.VacancyUrl,
                MessengerUrl = candidate.MessengerUrl,
                AvitoSubProfileId = candidate.AvitoSubProfileId,
                RawText = candidate.RawText,
                CreatedAt = candidate.CreatedAt == default ? DateTime.UtcNow : candidate.CreatedAt
            };

            var entity = new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = lead.AccountId,
                AccountName = lead.AccountName,
                Source = lead.Source,
                SourceResponseId = lead.SourceResponseId,
                FullName = lead.FullName,
                FirstName = lead.FirstName,
                LastName = lead.LastName,
                MiddleName = lead.MiddleName,
                Age = lead.Age,
                PhoneRaw = lead.PhoneRaw,
                PhoneNormalized = lead.PhoneNormalized,
                City = lead.City,
                Vacancy = lead.Vacancy,
                VacancyUrl = lead.VacancyUrl,
                MessengerUrl = lead.MessengerUrl,
                AvitoSubProfileId = lead.AvitoSubProfileId,
                RawText = lead.RawText,
                CreatedAt = lead.CreatedAt,
                Status = "InProgress",
                BitrixEntityType = bitrixOptions.Value.EntityType
            };

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                entity.Status = "Error";
                entity.ErrorMessage = "Не настроен вебхук Bitrix24 у администратора панели.";
                errors++;
            }
            else
            {
                var bitrixResult = await bitrixClient.CreateLeadAsync(
                    lead,
                    webhookUrl,
                    bitrixOptions.Value,
                    ct);
                entity.ProcessedAt = DateTime.UtcNow;
                if (bitrixResult.IsSuccess)
                {
                    entity.Status = "Sent";
                    entity.BitrixEntityId = bitrixResult.EntityId;
                    entity.BitrixContactId = bitrixResult.ContactId;
                    ingested++;
                }
                else
                {
                    entity.Status = "Error";
                    entity.ErrorMessage = bitrixResult.Error;
                    entity.BitrixContactId = bitrixResult.ContactId;
                    errors++;
                }
            }

            db.CandidateResponses.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        return new WorkerCandidateIngestionResultDto(received, ingested, skippedDuplicates, errors);
    }

    private async Task<string?> ResolveAdminWebhookUrlAsync(CancellationToken ct)
    {
        var adminUsers = await users.GetUsersInRoleAsync(PanelRoles.Admin);
        foreach (var admin in adminUsers.OrderBy(x => x.Email))
        {
            var settings = await db.PanelUserBitrixSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == admin.Id, ct);
            if (settings is null
                || string.IsNullOrWhiteSpace(settings.WebhookUrlProtected)
                || !string.Equals(settings.ValidationStatus, BitrixValidationStatuses.Ok, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                return protector.Unprotect(settings.WebhookUrlProtected);
            }
            catch
            {
                continue;
            }
        }

        return null;
    }
}