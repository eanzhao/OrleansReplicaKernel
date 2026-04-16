using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrleansReplicaKernel.App;

internal static class TraceLog
{
    private static readonly Lock Gate = new();
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private static readonly Dictionary<string, ILogger> Loggers = new(StringComparer.Ordinal);
    private static bool _useConsoleFallback = true;

    public static ILoggerFactory LoggerFactory
    {
        get
        {
            lock (Gate)
            {
                return _loggerFactory;
            }
        }
    }

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
        {
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _useConsoleFallback = loggerFactory is null;
            Loggers.Clear();
        }
    }

    public static void Write(string area, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);

        lock (Gate)
        {
            if (_useConsoleFallback)
            {
                Console.WriteLine($"[{area}] {message}");
                return;
            }

            if (!Loggers.TryGetValue(area, out var logger))
            {
                logger = _loggerFactory.CreateLogger("OrleansReplicaKernel." + area);
                Loggers.Add(area, logger);
            }

            logger.LogInformation("{Message}", message);
        }
    }
}
