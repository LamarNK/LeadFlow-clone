namespace Orbita.Tests;

public sealed class WorkerSettingsTemplatesMarkupTests
{
    [Fact]
    public void WorkerDetails_ContainsSettingsTemplatesBlock()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Workers/Details.cshtml");

        Assert.Contains("data-worker-settings-templates", view);
        Assert.Contains("Шаблоны настроек", view);
        Assert.Contains("data-worker-settings-template-select", view);
        Assert.Contains("data-worker-settings-template-apply", view);
        Assert.Contains("data-worker-settings-template-create", view);
        Assert.Contains("data-worker-settings-template-update", view);
        Assert.Contains("data-worker-settings-template-delete", view);
        Assert.Contains(">Применить<", view);
        Assert.Contains("Сохранить как новый", view);
        Assert.Contains(">Обновить<", view);
        Assert.Contains(">Удалить<", view);
        Assert.Contains("CreateSettingsTemplate", view);
        Assert.Contains("UpdateSettingsTemplate", view);
        Assert.Contains("DeleteSettingsTemplate", view);
        Assert.DoesNotContain("adsPowerApiKey", view[view.IndexOf("data-worker-settings-templates", StringComparison.Ordinal)..view.IndexOf("worker-settings-layout", StringComparison.Ordinal)]);
        Assert.DoesNotContain("multiloginAutomationToken", view[view.IndexOf("data-worker-settings-templates", StringComparison.Ordinal)..view.IndexOf("worker-settings-layout", StringComparison.Ordinal)]);
        Assert.DoesNotContain("responseHighlightTargets", view[view.IndexOf("data-worker-settings-templates", StringComparison.Ordinal)..view.IndexOf("worker-settings-layout", StringComparison.Ordinal)]);
    }

    [Fact]
    public void WorkerJs_AppliesTemplatesWithoutImmediateSave()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-worker.js");

        Assert.Contains("data-worker-settings-templates", js);
        Assert.Contains("applyTemplateSettings", js);
        Assert.Contains("collectTemplateSettings", js);
        Assert.Contains("Сохраните настройки воркера", js);
        Assert.DoesNotContain("adsPowerApiKey", js[js.IndexOf("collectTemplateSettings", StringComparison.Ordinal)..js.IndexOf("applyTemplateSettings", StringComparison.Ordinal)]);
        Assert.DoesNotContain("responseHighlightTargets", js[js.IndexOf("collectTemplateSettings", StringComparison.Ordinal)..js.IndexOf("applyTemplateSettings", StringComparison.Ordinal)]);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
