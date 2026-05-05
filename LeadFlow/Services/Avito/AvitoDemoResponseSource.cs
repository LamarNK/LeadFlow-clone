using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

public sealed class AvitoDemoResponseSource
{
    private readonly List<CandidateResponse> _seed =
    [
        Create("Романчук Антон Леонидович", "+7 918 579-23-60", "Краснодар", "Разнорабочий", 40),
        Create("Дунаева Елена Николаевна", "+7 922 345-67-89", "Екатеринбург", "Спецназ", 35),
        Create("Чижов Илья Сергеевич", "+7 920 172-23-21", "Новосибирск", "Оператор", 29),
        Create("Иванов Андрей Петрович", "+7 965 551-45-33", "Рязань", "Разнорабочий", 42),
        Create("Федоров Михаил Олегович", "+7 900 123-45-67", "Нижний Тагил", "Разнорабочий", 38)
    ];

    private int _offset;

    public Task<IReadOnlyList<CandidateResponse>> GetBatchAsync(AvitoAccount account, int max, CancellationToken cancellationToken)
    {
        var batch = new List<CandidateResponse>();
        for (var i = 0; i < max; i++)
        {
            var item = _seed[_offset % _seed.Count];
            _offset++;
            batch.Add(new CandidateResponse
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                FullName = item.FullName,
                PhoneRaw = item.PhoneRaw,
                City = item.City,
                Vacancy = item.Vacancy,
                Age = item.Age,
                Source = "Avito Demo",
                VacancyUrl = account.AvitoResponsesUrl,
                SourceUrl = account.AvitoResponsesUrl,
                SourceResponseId = $"demo-{_offset}",
                CreatedAt = DateTime.UtcNow
            });
        }

        return Task.FromResult<IReadOnlyList<CandidateResponse>>(batch);
    }

    private static CandidateResponse Create(string fullName, string phone, string city, string vacancy, int age) =>
        new()
        {
            FullName = fullName,
            PhoneRaw = phone,
            City = city,
            Vacancy = vacancy,
            Age = age
        };
}
