using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLoggerOptions
{
    public string BotToken { get; set; } = string.Empty;

    // Cambiamos ChatId individual por una lista de Admins
    public List<long> AdminChatIds { get; set; } = new();

    // Necesario para que el bot sepa dónde registrar su Webhook
    public string RenderUrl { get; set; } = string.Empty;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
}