using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class SubProfileStatusMapper
{
    public static (string Label, string Tone) ForDto(WorkerSubProfileDto subProfile)
    {
        if (!string.IsNullOrWhiteSpace(subProfile.LastIssueKind))
        {
            return ("Ошибка", "error");
        }

        if (!subProfile.IsEnabledInPanel)
        {
            return ("Выключен", "inactive");
        }

        if (subProfile.IsCurrent)
        {
            return ("Текущий", "success");
        }

        return ("Активен", "success");
    }

    public static (string Label, string Tone) ForRow(SubProfileRowViewModel subProfile)
    {
        if (subProfile.HasIssue)
        {
            return ("Ошибка", "error");
        }

        if (!subProfile.IsEnabledInPanel)
        {
            return ("Выключен", "inactive");
        }

        if (subProfile.IsCurrent)
        {
            return ("Текущий", "success");
        }

        return ("Активен", "success");
    }
}