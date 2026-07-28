using System.Reflection;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerMonitoringSettingsTests
{
    [Fact]
    public void ToAppSettings_AutoReplyDisabledInOrbit_DisablesAutoReply()
    {
        var settings = MapSettings(new WorkerMonitoringConfig());

        Assert.False(settings.Avito.MessengerAutoReply.Enabled);
    }

    [Fact]
    public void ToAppSettings_AutoReplyEnabledInOrbit_UsesOrbitSettings()
    {
        var settings = MapSettings(new WorkerMonitoringConfig
        {
            MessengerAutoReply = new AvitoMessengerAutoReplySettings
            {
                Enabled = true,
                Message = "Напишем вам позже."
            }
        });

        Assert.True(settings.Avito.MessengerAutoReply.Enabled);
        Assert.Equal("Напишем вам позже.", settings.Avito.MessengerAutoReply.Message);
    }

    private static AppSettings MapSettings(WorkerMonitoringConfig config)
    {
        var mapper = typeof(WorkerMonitoringService).GetMethod(
            "ToAppSettings",
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsType<AppSettings>(mapper?.Invoke(null, [config]));
    }
}
