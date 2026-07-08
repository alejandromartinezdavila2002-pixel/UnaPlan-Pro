using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLogger : ILogger
{
    private readonly string _categoryName;
    private readonly TelegramLoggerOptions _options;
    
    // Instancia estática para evitar el agotamiento de sockets (Socket Exhaustion)
    private static readonly HttpClient _httpClient = new HttpClient();

    public TelegramLogger(string categoryName, TelegramLoggerOptions options)
    {
        _categoryName = categoryName;
        _options = options;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel < _options.MinimumLevel) return false;
        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId)) return false;

        // Filtro anti-spam: Silenciamos los Warnings ruidosos y comunes de las librerías internas de Microsoft.
        // Solo nos interesan los Errores Reales de Microsoft, o los Warnings propios de nuestra App (UnaPlan).
        if (logLevel == LogLevel.Warning && _categoryName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        string emoji = logLevel switch
        {
            LogLevel.Critical => "🚨",
            LogLevel.Error => "❌",
            LogLevel.Warning => "⚠️",
            LogLevel.Information => "ℹ️",
            _ => "📝"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"{emoji} <b>{logLevel.ToString().ToUpper()}</b>");
        sb.AppendLine($"<b>App:</b> <code>UnaPlan API</code>");
        
        var shortCategory = _categoryName.Length > 40 ? "..." + _categoryName.Substring(_categoryName.Length - 37) : _categoryName;
        sb.AppendLine($"<b>Origen:</b> <code>{shortCategory}</code>");
        sb.AppendLine($"<b>Mensaje:</b> {System.Net.WebUtility.HtmlEncode(message)}");

        if (exception != null)
        {
            sb.AppendLine();
            sb.AppendLine("<pre><code class=\"language-text\">");
            var exStr = exception.ToString();
            if (exStr.Length > 2000) exStr = exStr.Substring(0, 2000) + "\n...[TRUNCADO]";
            sb.AppendLine(System.Net.WebUtility.HtmlEncode(exStr));
            sb.AppendLine("</code></pre>");
        }

        var telegramMessage = sb.ToString();

        _ = Task.Run(async () =>
        {
            try
            {
                var payload = new
                {
                    chat_id = _options.ChatId,
                    text = telegramMessage,
                    parse_mode = "HTML"
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
                
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[TelegramLogger] Error de la API de Telegram: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramLogger] Falló el envío de red a Telegram: {ex.Message}");
            }
        });
    }
}
