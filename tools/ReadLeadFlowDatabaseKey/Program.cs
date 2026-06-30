using LeadFlow.Services;

var dataDir = args.ElementAtOrDefault(0);
if (string.IsNullOrWhiteSpace(dataDir))
{
    dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
}

dataDir = Path.GetFullPath(dataDir);
if (!Directory.Exists(dataDir))
{
    Console.Error.WriteLine($"Папка не найдена: {dataDir}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Использование:");
    Console.Error.WriteLine("  ReadLeadFlowDatabaseKey.exe [путь_к_папке_Data]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Пример:");
    Console.Error.WriteLine(@"  ReadLeadFlowDatabaseKey.exe ""C:\LeadFlow\Data""");
    return 1;
}

var settingsPath = Path.Combine(dataDir, "LeadFlow.settings.dat");
if (!File.Exists(settingsPath))
{
    Console.Error.WriteLine($"Файл настроек не найден: {settingsPath}");
    return 1;
}

try
{
    var settings = await JsonSettingsService.LoadFromDataDirectoryAsync(dataDir);
    if (string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey))
    {
        Console.WriteLine("(пусто — база, вероятно, без шифрования; в Орбите оставьте поле ключа пустым)");
        return 0;
    }

    Console.WriteLine(settings.DatabaseEncryptionKey.Trim());
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось прочитать LeadFlow.settings.dat: {ex.Message}");
    return 1;
}