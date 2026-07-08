using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLoggerProvider : ILoggerProvider
{
    private readonly TelegramLoggerOptions _options;
    private readonly ConcurrentDictionary<string, TelegramLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public TelegramLoggerProvider(IOptionsMonitor<TelegramLoggerOptions> options)
    {
        _options = options.CurrentValue;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new TelegramLogger(name, _options));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}
