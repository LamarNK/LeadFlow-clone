using System.Reflection;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>
/// Гигиена генерируемых JS: в строках скриптов не должно быть window-глобалов __leadflow*,
/// data-атрибутов автоматизации и других палевных маркеров.
/// </summary>
public sealed class AvitoScriptHygieneTests
{
    [Fact]
    public void AllGeneratedScripts_AvoidLeadflowWindowGlobals()
    {
        var offenders = new List<string>();
        foreach (var script in BuildAllScripts())
        {
            if (script.Value.Contains("__leadflow", StringComparison.Ordinal)
                || script.Value.Contains("data-leadflow", StringComparison.Ordinal))
            {
                offenders.Add(script.Key);
            }
        }

        Assert.True(offenders.Count == 0, $"Скрипты с __leadflow/data-leadflow: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void AllGeneratedScripts_UseSymbolStateStore()
    {
        // Все скрипты, которым нужно состояние, должны идти через lfState() (Symbol-контейнер).
        var stateScripts = BuildAllScripts()
            .Where(static s => s.Value.Contains("revealedPhones", StringComparison.Ordinal)
                || s.Value.Contains("skipPhoneReveal", StringComparison.Ordinal)
                || s.Value.Contains("phoneWatchPriority", StringComparison.Ordinal)
                || s.Value.Contains("scrollBoundary", StringComparison.Ordinal)
                || s.Value.Contains("detailEnrichment", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(stateScripts);
        foreach (var script in stateScripts)
        {
            Assert.Contains("lfState", script.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoScript_PatchesNativeClipboard()
    {
        foreach (var script in BuildAllScripts())
        {
            Assert.False(
                script.Value.Contains("navigator.clipboard", StringComparison.Ordinal)
                && script.Value.Contains("writeText", StringComparison.Ordinal)
                && script.Value.Contains("= async", StringComparison.Ordinal),
                $"Скрипт {script.Key} патчит navigator.clipboard.");
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildAllScripts()
    {
        var types = new[] { typeof(AvitoCandidatesPageScripts), typeof(AvitoAdListPageScripts) };
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(string) || method.GetParameters().Length != 0)
                {
                    continue;
                }

                string value;
                try
                {
                    value = (string)method.Invoke(null, null)!;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(value))
                {
                    yield return new KeyValuePair<string, string>($"{type.Name}.{method.Name}", value);
                }
            }
        }

        // Константы скриптов (const string fields/properties).
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(string) && field.GetRawConstantValue() is not null)
                {
                    var value = (string?)field.GetValue(null);
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return new KeyValuePair<string, string>($"{type.Name}.{field.Name}", value);
                    }
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(string) && field.IsInitOnly)
                {
                    var value = (string?)field.GetValue(null);
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return new KeyValuePair<string, string>($"{type.Name}.{field.Name}", value);
                    }
                }
            }
        }
    }
}
