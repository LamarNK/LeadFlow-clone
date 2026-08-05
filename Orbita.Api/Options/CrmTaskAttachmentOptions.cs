namespace Orbita.Api.Options;

public sealed class CrmTaskAttachmentOptions
{
    public const string SectionName = "CrmTaskAttachments";

    public string DataPath { get; set; } = "Data/crm/task-attachments";

    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
}
