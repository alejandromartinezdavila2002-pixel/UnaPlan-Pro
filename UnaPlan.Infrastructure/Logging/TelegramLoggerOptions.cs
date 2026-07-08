using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLoggerOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
}
