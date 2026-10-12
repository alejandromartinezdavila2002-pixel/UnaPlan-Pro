using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public const string BotonBuscarTps = "🔎 Buscar TPs ahora";

    // 🧠 Memoria volátil: Falso por defecto cada vez que el servidor arranca
    public bool ModoEnVivoActivado { get; private set; } = false;

    public TelegramControlService(IConfiguration config, ILogger<TelegramControlService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;

        var token = config["Telegram:BotToken"] ?? throw new ArgumentNullException("Telegram:BotToken no configurado en los secretos.");
        _botClient = new TelegramBotClient(token);

        // Hacemos la lectura robusta: Si viene como un string separado por comas (típico en variables de entorno), lo parseamos.
        // Si no, intentamos leerlo como lista directa (típico en appsettings.json).
        var adminIdsString = config["Telegram:AdminChatIds"];
        if (!string.IsNullOrEmpty(adminIdsString))
        {
            _adminChatIds = adminIdsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(id => long.Parse(id.Trim()))
                                          .ToList();
        }
        else
        {
            _adminChatIds = config.GetSection("Telegram:AdminChatIds").Get<List<long>>() ?? new List<long>();
        }
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

        // 🔎 Búsqueda manual de TPs
        if (comandoTexto == BotonBuscarTps)
        {
            await IniciarBusquedaManualTpAsync(chatId);
            return;
        }

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

    // 2B. Búsqueda manual de TPs desde Telegram
    private async Task IniciarBusquedaManualTpAsync(long chatId)
    {
        await EnviarSeguroAsync(chatId, "⏳ <b>Buscando TPs pendientes en Drive...</b>\n<i>Te aviso aquí cuando termine.</i>");

        // Corre en segundo plano: el webhook debe devolver 200 rápido o Telegram reintenta
        // el mismo update y dispararía la búsqueda varias veces.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();
                var resultado = await scraper.EscanearTpPendientesAsync(esperarSiOcupado: false);

                await EnviarSeguroAsync(chatId, FormatearResultadoTp(resultado));
                _logger.LogInformation($"[Telegram] Búsqueda manual de TPs ejecutada por {chatId}: {resultado.Encontrados.Count} encontrados.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Telegram] Error en la búsqueda manual de TPs.");
                await EnviarSeguroAsync(chatId, $"❌ <b>Error buscando TPs:</b> {System.Net.WebUtility.HtmlEncode(ex.Message)}");
            }
        });
    }

    public static string FormatearResultadoTp(ResultadoEscaneoTp r)
    {
        if (r.EscaneoEnCurso)
            return "⚠️ Ya hay un escaneo de TPs en curso (manual o del vigilante). Intenta de nuevo en unos minutos.";

        if (r.PendientesRevisados == 0)
            return "🏆 <b>Semestre al día.</b> No hay TPs pendientes en la base de datos.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔎 <b>Búsqueda manual de TPs terminada</b>");
        sb.AppendLine($"Revisados: {r.PendientesRevisados} · Encontrados: {r.Encontrados.Count} · Alertas: {r.AlertasEnviadas}");

        AgregarLista(sb, "✅ Publicados ahora", r.Encontrados);
        AgregarLista(sb, "⏳ Siguen sin aparecer", r.SiguenPendientes);

        return sb.ToString();
    }

    private static void AgregarLista(System.Text.StringBuilder sb, string titulo, List<string> items)
    {
        if (items.Count == 0) return;
        const int max = 25; // Telegram corta en 4096 caracteres

        sb.AppendLine().AppendLine($"<b>{titulo}:</b>");
        foreach (var item in items.Take(max))
            sb.AppendLine($"• {System.Net.WebUtility.HtmlEncode(item)}");
        if (items.Count > max)
            sb.AppendLine($"<i>...y {items.Count - max} más</i>");
    }

    private async Task EnviarSeguroAsync(long chatId, string html)
    {
        try
        {
            await _botClient.SendMessage(
                chatId: chatId,
                text: html,
                parseMode: ParseMode.Html,
                replyMarkup: CrearTecladoDinamico()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error enviando mensaje a Telegram: {ex.Message}");
        }
    }

    // 3. Constructor del teclado persistente en la parte inferior
    private ReplyKeyboardMarkup CrearTecladoDinamico()
    {
        string textoBoton = ModoEnVivoActivado ? "🔴 Apagar Logs en Vivo" : "🟢 Encender Logs en Vivo";

        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { textoBoton },
            new KeyboardButton[] { BotonBuscarTps }
        })
        {
            ResizeKeyboard = true, // Hace que el teclado se ajuste bonito y no ocupe media pantalla
            IsPersistent = true    // Le dice a Telegram que mantenga este teclado siempre abierto
        };
    }
}