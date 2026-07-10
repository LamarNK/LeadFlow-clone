using Microsoft.AspNetCore.Http;

namespace Orbita.Web.Helpers;

internal static class FormBindingHelper
{
    public static bool ReadCheckbox(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values)
        && values.Count > 0
        && values.Contains("true", StringComparer.OrdinalIgnoreCase);
}