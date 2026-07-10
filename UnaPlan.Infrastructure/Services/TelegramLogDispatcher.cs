using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Bot;
using Microsoft.Extensions.Configuration;

namespace UnaPlan.Infrastructure.Services;

public record TelegramLogMessage(string HtmlText, Telegram.Bot.Types.ReplyMarkups.ReplyMarkup? ReplyMarkup);

public class TelegramLogDispatcher : BackgroundService
{
    private readonly Channel<TelegramLogMessage> _logQueue;
    private readonly TelegramControlService _controlService;
    private readonly ITelegramBotClient _botClient;
    private readonly List<long> _adminChatIds;
    private readonly ILogger<TelegramLogDispatcher> _logger;

    public TelegramLogDispatcher(
        TelegramControlService controlService, 
        IConfiguration config,
        ILogger<TelegramLogDispatcher> logger)
    {
        _logQueue = Channel.CreateUnbounded<TelegramLogMessage>();
        _controlService = controlService;
        _logger = logger;
        _adminChatIds = config.GetSection("Telegram:AdminChatIds").Get<List<long>>() ?? new List<long>();
        
        var token = config["Telegram:BotToken"];
        _botClient = new Telegram.Bot.TelegramBotClient(token!);
    }

    // Método para que el NotionWorkerService llame y encole el log
    public void EnqueueLog(string message, Telegram.Bot.Types.ReplyMarkups.ReplyMarkup? replyMarkup = null)
    {
        // Solo encolamos si el interruptor en Telegram está encendido
        if (_controlService.ModoEnVivoActivado)
        {
            _logQueue.Writer.TryWrite(new TelegramLogMessage(message, replyMarkup));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Esperamos a que llegue un log a la cola
            var logMessage = await _logQueue.Reader.ReadAsync(stoppingToken);

            // Si el interruptor se apagó mientras esperábamos, descartamos
            if (_controlService.ModoEnVivoActivado)
            {
                foreach (var chatId in _adminChatIds)
                {
                    try 
                    { 
                        await _botClient.SendMessage(
                            chatId: chatId, 
                            text: logMessage.HtmlText,
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                            replyMarkup: logMessage.ReplyMarkup); 
                    }
                    catch (Exception ex) { _logger.LogError($"Error enviando log a Telegram: {ex.Message}"); }
                }

                // 🔥 Aquí aplicamos el buffer de 0.5 segundos para no ser bloqueados
                await Task.Delay(500, stoppingToken);
            }
        }
    }
}