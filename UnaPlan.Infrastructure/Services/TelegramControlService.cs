using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;

namespace UnaPlan.Infrastructure.Services;

public class TelegramControlService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramControlService> _logger;
    private readonly List<long> _adminChatIds;

    // 🧠 Memoria volátil: Falso por defecto cada vez que el servidor arranca
    public bool ModoEnVivoActivado { get; private set; } = false;

    public TelegramControlService(IConfiguration config, ILogger<TelegramControlService> logger)
    {
        _logger = logger;

        var token = config["Telegram:BotToken"] ?? throw new ArgumentNullException("Telegram:BotToken no configurado en los secretos.");
        _botClient = new TelegramBotClient(token);

        // Obtenemos la lista de admins para seguridad (El Portero)
        _adminChatIds = config.GetSection("Telegram:AdminChatIds").Get<List<long>>() ?? new List<long>();
    }

    // 1. Enviar el panel principal al arrancar el servidor
    public async Task EnviarPanelDeControlAsync()
    {
        if (_adminChatIds.Count == 0) return;

        var teclado = CrearTecladoDinamico();

        foreach (var adminId in _adminChatIds)
        {
            try
            {
                await _botClient.SendMessage(
                    chatId: adminId,
                    text: "🤖 *API UNA Plan - Sistema Iniciado*\n\nServidor en línea. Utiliza el teclado inferior para controlar los módulos.",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: teclado
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"No se pudo enviar el panel al admin {adminId}: {ex.Message}");
            }
        }
    }

    // 2. Procesar el texto enviado por los botones del teclado inferior
    public async Task ProcesarComandoTecladoAsync(long chatId, string comandoTexto)
    {
        // Seguridad estricta
        if (!_adminChatIds.Contains(chatId)) return;

        // Lógica del interruptor basado en el texto del botón
        if (comandoTexto == "🟢 Encender Logs en Vivo" && !ModoEnVivoActivado)
        {
            ModoEnVivoActivado = true;
        }
        else if (comandoTexto == "🔴 Apagar Logs en Vivo" && ModoEnVivoActivado)
        {
            ModoEnVivoActivado = false;
        }
        else
        {
            // Si escriben otra cosa, ignoramos la acción pero reenviamos el menú por si se les perdió
        }

        var nuevoTeclado = CrearTecladoDinamico();
        string estadoTexto = ModoEnVivoActivado
            ? "🟢 *Modo en Vivo: ENCENDIDO*\n_Mostrando logs de la API en tiempo real..._"
            : "🔴 *Modo en Vivo: APAGADO*\n_Silencio en la sala._";

        try
        {
            // Para actualizar un teclado inferior (ReplyKeyboard), OBLIGATORIAMENTE debemos enviar un nuevo mensaje.
            await _botClient.SendMessage(
                chatId: chatId,
                text: estadoTexto,
                parseMode: ParseMode.Markdown,
                replyMarkup: nuevoTeclado
            );

            _logger.LogInformation($"[Telegram] El Administrador {chatId} cambió el Modo En Vivo a: {ModoEnVivoActivado}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error al actualizar el teclado de Telegram: {ex.Message}");
        }
    }

    // 3. Constructor del teclado persistente en la parte inferior
    private ReplyKeyboardMarkup CrearTecladoDinamico()
    {
        string textoBoton = ModoEnVivoActivado ? "🔴 Apagar Logs en Vivo" : "🟢 Encender Logs en Vivo";

        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { textoBoton }
        })
        {
            ResizeKeyboard = true, // Hace que el teclado se ajuste bonito y no ocupe media pantalla
            IsPersistent = true    // Le dice a Telegram que mantenga este teclado siempre abierto
        };
    }
}