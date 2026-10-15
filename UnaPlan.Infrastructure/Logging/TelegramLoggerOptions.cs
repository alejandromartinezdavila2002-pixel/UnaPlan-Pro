using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLoggerOptions
{
    public string BotToken { get; set; } = string.Empty;

    // Lista de admins en vez de un solo ChatId
    public List<long> AdminChatIds { get; set; } = new();

    // URL donde el bot registra su webhook
    public string RenderUrl { get; set; } = string.Empty;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
}