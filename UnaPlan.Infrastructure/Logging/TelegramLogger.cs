using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using UnaPlan.Infrastructure.Services;

namespace UnaPlan.Infrastructure.Logging;

public class TelegramLogger : ILogger
{
    private readonly string _categoryName;
    private readonly TelegramLoggerOptions _options;
    private readonly System.IServiceProvider _serviceProvider;


    public TelegramLogger(string categoryName, TelegramLoggerOptions options, System.IServiceProvider serviceProvider)
    {
        _categoryName = categoryName;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel < _options.MinimumLevel) return false;
        if (string.IsNullOrWhiteSpace(_options.BotToken) || _options.AdminChatIds == null || _options.AdminChatIds.Count == 0) return false;

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

        // Detección de mensajes tipo Batch (Resumen + Botón oculto)
        bool isBatch = false;
        string batchDetails = "";
        string? callbackId = null;

        if (eventId.Id == 999 && message.Contains("|BATCH_DETAILS|"))
        {
            isBatch = true;
            var parts = message.Split("|BATCH_DETAILS|");
            message = parts[0]; // Nos quedamos solo con el resumen
            if (parts.Length > 1) 
            {
                batchDetails = parts[1];
                callbackId = Guid.NewGuid().ToString("N");
                
                // Resolución "Lazy" (Diferida) para evitar la Dependencia Circular en el arranque
                var memoryCache = _serviceProvider.GetService<IMemoryCache>();
                memoryCache?.Set(callbackId, batchDetails, TimeSpan.FromHours(24));
            }
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

        Telegram.Bot.Types.ReplyMarkups.ReplyMarkup? replyMarkup = null;
        if (isBatch && callbackId != null)
        {
            replyMarkup = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                new[]
                {
                    new[] { Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔍 Ver detalles técnicos", callbackId) }
                }
            );
        }

        // Resolución "Lazy" del Dispatcher para evitar Dependencia Circular
        var dispatcher = _serviceProvider.GetService<TelegramLogDispatcher>();
        if (dispatcher != null)
        {
            // Los logs que llegan a TelegramLogger ya están filtrados (son Errores, Warnings o Batches importantes).
            // Por lo tanto, SIEMPRE los enviamos, ignorando si el Modo En Vivo está apagado.
            dispatcher.EnqueueLog(telegramMessage, replyMarkup, bypassLiveMode: true);
        }
        else
        {
            // Fallback (Solo si el Dispatcher no pudo resolverse durante el inicio)
            Console.WriteLine($"[TelegramLogger Fallback] {telegramMessage.Replace("<b>", "").Replace("</b>", "")}");
        }
    }
}
