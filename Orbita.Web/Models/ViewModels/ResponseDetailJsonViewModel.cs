namespace Orbita.Web.Models.ViewModels;

public sealed class ResponseDetailJsonViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public Guid ResponseId { get; init; }
    public bool CanEdit { get; init; }
    public ResponseDetailEditViewModel? Edit { get; init; }
    public ResponseDetailProfileViewModel Profile { get; init; } = new();
    public IReadOnlyList<DetailSectionItemViewModel> Sections { get; init; } = [];
    public IReadOnlyList<DetailChatMessageViewModel> ChatMessages { get; init; } = [];
    public IReadOnlyList<DetailActionLinkViewModel> Links { get; init; } = [];
    public IReadOnlyList<DetailActionLinkViewModel> PrimaryActions { get; init; } = [];
    public string? CopyText { get; init; }
    public string CopyLabel { get; init; } = "Копировать карточку";
}

public sealed class ResponseDetailEditViewModel
{
    public string FullName { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string Gender { get; init; } = string.Empty;
}

public sealed class ResponseDetailProfileViewModel
{
    public string CandidateName { get; init; } = string.Empty;
    public string CandidateMeta { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "unique";
    public string Phone { get; init; } = string.Empty;
    public string? PhoneHref { get; init; }
    public string? MessengerUrl { get; init; }
    public string? AvatarUrl { get; init; }
    public string Vacancy { get; init; } = string.Empty;
    public string? VacancyUrl { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? Office { get; init; }
}

public sealed class DetailSectionItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public IReadOnlyList<string> Values { get; init; } = [];
    public string? Href { get; init; }
}

public sealed class DetailChatMessageViewModel
{
    public string Text { get; init; } = string.Empty;
    public string Tone { get; init; } = "incoming";
    public string? TimeLabel { get; init; }
}

public sealed class DetailActionLinkViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
    public bool External { get; init; }
    public string Tone { get; init; } = "secondary";
    public string Action { get; init; } = "link";
    public Guid? ResponseId { get; init; }
}
