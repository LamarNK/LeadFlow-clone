using LeadFlow.Logging.Audit;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: LogProbe <log-directory> [take]");
    return 1;
}

var logDirectory = args[0];
var take = 40;
if (args.Length > 1 && int.TryParse(args[1], out var parsedTake) && parsedTake > 0)
{
    take = parsedTake;
}

var logger = new Logger(logDirectory);
var logs = await logger.SearchLogsAsync();

foreach (var log in logs.Take(take))
{
    Console.WriteLine($"{log.Timestamp:O} | {log.Level,-7} | {log.Prefix} | {log.Message}");
    if (!string.IsNullOrWhiteSpace(log.Properties))
    {
        Console.WriteLine($"  props: {log.Properties}");
    }

    if (log.IsTampered)
    {
        Console.WriteLine($"  tampered: {log.TamperReason}");
    }
}

return 0;
