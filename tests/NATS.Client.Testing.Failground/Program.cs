﻿using System.Diagnostics;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using NATS.Client.Testing.Failground;
using ZLogger;

var runId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}";

using var loggerFactory = LoggerFactory.Create(configure: builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Debug)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
        })
        .AddZLoggerFile($"test_{runId}.log", configure: options =>
        {
            options.FlushRate = TimeSpan.FromSeconds(1);
            options.PrefixFormatter = (writer, info) => ZString.Utf8Format(
                writer,
                "{0} {1} [{2}] ({3}) ",
                info.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                info.LogLevel,
                info.CategoryName,
                info.EventId.Id);
        });
});

var logger = loggerFactory.CreateLogger("Program");
logger.LogInformation("Starting...");

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    if (!cts.IsCancellationRequested)
    {
        e.Cancel = true;
        Console.Error.WriteLine("Stopping...");
        cts.Cancel();
    }
    else
    {
        Console.Error.WriteLine("Aborting...");
    }
};

var tests = new Dictionary<string, ITest>
{
    { "consumer", new ConsumeTest(loggerFactory) },
    { "pub-sub", new PubSubTest(loggerFactory) },
};

if (args.Length != 1 || !tests.TryGetValue(args[0], out var test))
{
    Console.Error.WriteLine($"Usage: {Process.GetCurrentProcess().ProcessName} <test>");
    Console.Error.WriteLine("  Available tests: " + string.Join(", ", tests.Keys));
    return 2;
}

try
{
    logger.LogInformation("Starting test {Name} ({RunId})...", test.GetType().Name, runId);

    await test.Run(runId, cts.Token);

    return 0;
}
catch (Exception e)
{
    logger.LogError(e, "Error running test {Name}", test.GetType().Name);
    return 1;
}