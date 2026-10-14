using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using UnaPlan.Core.Entities;

namespace UnaPlan.Infrastructure.Services;

public class TelegramControlService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramControlService> _logger;
    private readonly List<long> _adminChatIds;
    private readonly IServiceScopeFactory _scopeFactory;

    // PIN del panel de Periodo Académico. Vive SOLO en la variable de entorno
    // Telegram__PinPeriodoAcademico (Render) / user-secrets (local). Nunca en el repo.
    private readonly string? _pinPeriodo;

    public const string BotonBuscarTps = "🔎 Buscar TPs ahora";
    public const string BotonPeriodoAcademico = "🎓 Periodo Académico";
    private const string BotonEncenderLogs = "🟢 Encender Logs en Vivo";
    private const string BotonApagarLogs = "🔴 Apagar Logs en Vivo";

    // 🧠 Memoria volátil: Falso por defecto cada vez que el servidor arranca
    public bool ModoEnVivoActivado { get; private set; } = false;

    public TelegramControlService(IConfiguration config, ILogger<TelegramControlService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;

        var token = config["Telegram:BotToken"] ?? throw new ArgumentNullException("Telegram:BotToken no configurado en los secretos.");
        _botClient = new TelegramBotClient(token);

        _pinPeriodo = config["Telegram:PinPeriodoAcademico"]?.Trim();

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

    public bool EsAdmin(long id) => _adminChatIds.Contains(id);

    // ========================================================================
    // 1. PANEL PRINCIPAL (al arrancar el servidor)
    // ========================================================================
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

    // ========================================================================
    // 2. ENTRADA DE MENSAJES (texto del teclado, pasos del panel o PDF)
    // ========================================================================
    public async Task ProcesarMensajeAsync(Message mensaje)
    {
        var chatId = mensaje.Chat.Id;
        if (!EsAdmin(chatId)) return;

        // 2.1 Documento (PDF del calendario)
        if (mensaje.Document != null)
        {
            await ProcesarDocumentoAsync(chatId, mensaje.Document);
            return;
        }

        var texto = mensaje.Text?.Trim();
        if (string.IsNullOrEmpty(texto)) return;

        var sesion = ObtenerSesion(chatId);
        bool hayProcesoEnCurso = sesion is { Paso: PasoPanel.Procesando or PasoPanel.AnalizandoPdf };

        // 2.2 Botón del panel y /cancelar: cortan cualquier flujo abierto (salvo uno ya ejecutándose)
        if (texto == BotonPeriodoAcademico || texto == "/cancelar")
        {
            if (hayProcesoEnCurso)
            {
                await EnviarSeguroAsync(chatId, "⏳ Hay un proceso del panel en curso. Te aviso aquí cuando termine.");
                return;
            }

            TerminarSesion(chatId);

            if (texto == BotonPeriodoAcademico) await MostrarSubmenuPeriodoAsync(chatId);
            else await EnviarSeguroAsync(chatId, "❌ Operación cancelada.");
            return;
        }

        bool esBotonTeclado = texto == BotonBuscarTps || texto == BotonEncenderLogs || texto == BotonApagarLogs;

        // 2.3 Respuesta a un paso del panel (PIN, nombre, correo, códigos)
        if (!esBotonTeclado && sesion != null)
        {
            await ProcesarPasoTextoAsync(chatId, mensaje.MessageId, texto, sesion);
            return;
        }

        // 2.4 Comportamiento original del teclado
        await ProcesarComandoTecladoAsync(chatId, texto);
    }

    // 2B. Procesar el texto enviado por los botones del teclado inferior
    public async Task ProcesarComandoTecladoAsync(long chatId, string comandoTexto)
    {
        // Seguridad estricta
        if (!EsAdmin(chatId)) return;

        // 🔎 Búsqueda manual de TPs
        if (comandoTexto == BotonBuscarTps)
        {
            await IniciarBusquedaManualTpAsync(chatId);
            return;
        }

        // Lógica del interruptor basado en el texto del botón
        if (comandoTexto == BotonEncenderLogs && !ModoEnVivoActivado)
        {
            ModoEnVivoActivado = true;
        }
        else if (comandoTexto == BotonApagarLogs && ModoEnVivoActivado)
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

    // 2C. Búsqueda manual de TPs desde Telegram
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
                await EnviarSeguroAsync(chatId, $"❌ <b>Error buscando TPs:</b> {Html(ex.Message)}");
            }
        });
    }

    public static string FormatearResultadoTp(ResultadoEscaneoTp r)
    {
        if (r.EscaneoEnCurso)
            return "⚠️ Ya hay un escaneo de TPs en curso (manual o del vigilante). Intenta de nuevo en unos minutos.";

        if (r.PendientesRevisados == 0)
            return "🏆 <b>Semestre al día.</b> No hay TPs pendientes en la base de datos.";

        var sb = new StringBuilder();
        sb.AppendLine($"🔎 <b>Búsqueda manual de TPs terminada</b>");
        sb.AppendLine($"Revisados: {r.PendientesRevisados} · Encontrados: {r.Encontrados.Count} · Alertas: {r.AlertasEnviadas}");

        AgregarLista(sb, "✅ Publicados ahora", r.Encontrados);
        AgregarLista(sb, "⏳ Siguen sin aparecer", r.SiguenPendientes);

        return sb.ToString();
    }

    private static void AgregarLista(StringBuilder sb, string titulo, List<string> items)
    {
        if (items.Count == 0) return;
        const int max = 25; // Telegram corta en 4096 caracteres

        sb.AppendLine().AppendLine($"<b>{titulo}:</b>");
        foreach (var item in items.Take(max))
            sb.AppendLine($"• {Html(item)}");
        if (items.Count > max)
            sb.AppendLine($"<i>...y {items.Count - max} más</i>");
    }

    // ========================================================================
    // 3. PANEL "PERIODO ACADÉMICO" — sesión por chat
    // ========================================================================
    public const string PrefijoPanel = "pa:";
    private const string CbIniciarPeriodo = "pa:iniciar";
    private const string CbPlanEstudiante = "pa:plan";
    private const string CbConfirmarPeriodo = "pa:ok-periodo";
    private const string CbConfirmarPlan = "pa:ok-plan";
    private const string CbCancelar = "pa:cancelar";
    private const string CbEstadoConvocatoria = "pa:conv";
    private const string CbEnviarConvocatoria = "pa:conv-enviar";

    private static readonly TimeSpan DuracionSesion = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DuracionBloqueoPin = TimeSpan.FromMinutes(15);
    private const int MaxIntentosPin = 3;
    private const long MaxBytesPdf = 20L * 1024 * 1024; // Límite de descarga de la Bot API

    private static readonly Regex CorreoRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private enum PasoPanel
    {
        EsperandoPin, EsperandoPdf, AnalizandoPdf, EsperandoConfirmacionPeriodo,
        EsperandoNombre, EsperandoCorreo, EsperandoCodigos, EsperandoConfirmacionPlan,
        Procesando
    }

    private sealed class SesionPanel
    {
        public PasoPanel Paso { get; set; }
        public DateTime UltimaActividad { get; set; } = DateTime.UtcNow;
        public CalendarioProcesado? Calendario { get; set; }
        public string Nombre { get; set; } = "";
        public string Correo { get; set; } = "";
        public string Codigos { get; set; } = "";
    }

    private sealed class ControlPin
    {
        public int Fallos;
        public DateTime? BloqueadoHasta;
    }

    // En memoria: si Render reinicia, la sesión se pierde y basta con empezar de nuevo
    private readonly ConcurrentDictionary<long, SesionPanel> _sesiones = new();
    private readonly ConcurrentDictionary<long, ControlPin> _controlPin = new();

    public static bool EsCallbackDelPanel(string? data) =>
        data != null && data.StartsWith(PrefijoPanel, StringComparison.Ordinal);

    private SesionPanel? ObtenerSesion(long chatId)
    {
        if (!_sesiones.TryGetValue(chatId, out var s)) return null;

        bool enEjecucion = s.Paso is PasoPanel.Procesando or PasoPanel.AnalizandoPdf;
        if (!enEjecucion && DateTime.UtcNow - s.UltimaActividad > DuracionSesion)
        {
            TerminarSesion(chatId, s);
            return null;
        }
        return s;
    }

    private SesionPanel NuevaSesion(long chatId, PasoPanel paso)
    {
        var s = new SesionPanel { Paso = paso };
        _sesiones[chatId] = s;
        return s;
    }

    private void TerminarSesion(long chatId) => _sesiones.TryRemove(chatId, out _);

    // Solo borra si sigue siendo la misma sesión (no pisa una nueva que se abrió mientras tanto)
    private void TerminarSesion(long chatId, SesionPanel s) =>
        _sesiones.TryRemove(new KeyValuePair<long, SesionPanel>(chatId, s));

    private Task MostrarSubmenuPeriodoAsync(long chatId)
    {
        var teclado = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🚀 Iniciar nuevo periodo académico", CbIniciarPeriodo) },
            new[] { InlineKeyboardButton.WithCallbackData("📝 Generar plan a estudiante", CbPlanEstudiante) },
            new[] { InlineKeyboardButton.WithCallbackData("📣 Estado de la convocatoria", CbEstadoConvocatoria) },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Cerrar", CbCancelar) }
        });

        return EnviarSeguroAsync(chatId, "🎓 <b>Panel de Periodo Académico</b>\n\n¿Qué quieres hacer?", teclado);
    }

    // ========================================================================
    // 4. BOTONES INLINE DEL PANEL (prefijo "pa:")
    // ========================================================================
    public async Task ProcesarCallbackAsync(CallbackQuery query)
    {
        var chatId = query.Message?.Chat.Id ?? query.From.Id;

        // Misma regla que los mensajes: solo chats de administradores
        if (!EsAdmin(chatId))
        {
            await ResponderCallbackAsync(query.Id, "⛔ No autorizado");
            return;
        }

        await ResponderCallbackAsync(query.Id);

        // Quitamos los botones del mensaje pulsado para que no se puedan volver a tocar
        if (query.Message != null)
            await QuitarBotonesAsync(chatId, query.Message.MessageId);

        switch (query.Data)
        {
            case CbIniciarPeriodo:
                await IniciarFlujoPeriodoAsync(chatId);
                break;

            case CbPlanEstudiante:
                NuevaSesion(chatId, PasoPanel.EsperandoNombre);
                await EnviarSeguroAsync(chatId,
                    "📝 <b>Generar plan a estudiante</b>\n\nEscribe el <b>nombre completo</b> del estudiante.",
                    BotonesCancelar());
                break;

            case CbConfirmarPeriodo:
                await ConfirmarPeriodoAsync(chatId);
                break;

            case CbConfirmarPlan:
                await ConfirmarPlanAsync(chatId);
                break;

            case CbEstadoConvocatoria:
                await MostrarEstadoConvocatoriaAsync(chatId);
                break;

            case CbEnviarConvocatoria:
                await EnviarConvocatoriaManualAsync(chatId);
                break;

            case CbCancelar:
                if (ObtenerSesion(chatId) is { Paso: PasoPanel.Procesando })
                {
                    await EnviarSeguroAsync(chatId, "⏳ El proceso ya se está ejecutando y no se puede cancelar. Te aviso al terminar.");
                    break;
                }
                TerminarSesion(chatId);
                await EnviarSeguroAsync(chatId, "❌ Operación cancelada.");
                break;
        }
    }

    // ========================================================================
    // 5. PASOS DE TEXTO (PIN, nombre, correo, códigos)
    // ========================================================================
    private async Task ProcesarPasoTextoAsync(long chatId, int messageId, string texto, SesionPanel sesion)
    {
        sesion.UltimaActividad = DateTime.UtcNow;

        switch (sesion.Paso)
        {
            case PasoPanel.EsperandoPin:
                await ValidarPinAsync(chatId, messageId, texto, sesion);
                break;

            case PasoPanel.EsperandoPdf:
                await EnviarSeguroAsync(chatId, "📎 Estoy esperando el <b>PDF del calendario</b>. Envíalo como archivo, o escribe /cancelar.");
                break;

            case PasoPanel.AnalizandoPdf:
            case PasoPanel.Procesando:
                await EnviarSeguroAsync(chatId, "⏳ Hay un proceso en curso. Te aviso aquí cuando termine.");
                break;

            case PasoPanel.EsperandoConfirmacionPeriodo:
            case PasoPanel.EsperandoConfirmacionPlan:
                await EnviarSeguroAsync(chatId, "👆 Usa los botones <b>Confirmar</b> o <b>Cancelar</b> del mensaje anterior.");
                break;

            case PasoPanel.EsperandoNombre:
                if (texto.Length < 3 || texto.Length > 120)
                {
                    await EnviarSeguroAsync(chatId, "❌ El nombre debe tener entre 3 y 120 caracteres. Escríbelo de nuevo.");
                    break;
                }
                sesion.Nombre = texto;
                sesion.Paso = PasoPanel.EsperandoCorreo;
                await EnviarSeguroAsync(chatId, $"📧 Escribe el <b>correo electrónico</b> de {Html(texto)}.", BotonesCancelar());
                break;

            case PasoPanel.EsperandoCorreo:
                if (!CorreoRegex.IsMatch(texto))
                {
                    await EnviarSeguroAsync(chatId, "❌ Ese correo no parece válido. Escríbelo de nuevo.");
                    break;
                }
                sesion.Correo = texto;
                sesion.Paso = PasoPanel.EsperandoCodigos;
                await EnviarSeguroAsync(chatId,
                    "📚 Escribe los <b>códigos de las materias</b> separados por coma.\nEjemplo: <code>115, 324, 735</code>",
                    BotonesCancelar());
                break;

            case PasoPanel.EsperandoCodigos:
                var codigos = SuscripcionService.ParsearMaterias(texto);
                if (codigos.Count == 0)
                {
                    await EnviarSeguroAsync(chatId, "❌ No reconocí ningún código. Ejemplo: <code>115, 324, 735</code>");
                    break;
                }
                sesion.Codigos = string.Join(", ", codigos);
                sesion.Paso = PasoPanel.EsperandoConfirmacionPlan;

                var resumen = new StringBuilder();
                resumen.AppendLine("📝 <b>Confirmar plan de evaluación</b>");
                resumen.AppendLine();
                resumen.AppendLine($"Estudiante: <b>{Html(sesion.Nombre)}</b>");
                resumen.AppendLine($"Correo: <code>{Html(sesion.Correo)}</code>");
                resumen.AppendLine($"Materias: <code>{Html(sesion.Codigos)}</code>");
                resumen.AppendLine();
                resumen.AppendLine("<i>Se generará el Excel, se enviará por correo y se suscribirá al estudiante a las alertas de TP/TSP.</i>");

                await EnviarSeguroAsync(chatId, resumen.ToString(), BotonesConfirmar("✅ Enviar plan", CbConfirmarPlan));
                break;
        }
    }

    // ========================================================================
    // 6. FLUJO "INICIAR NUEVO PERIODO" — PIN
    // ========================================================================
    private async Task IniciarFlujoPeriodoAsync(long chatId)
    {
        // Falla cerrado: sin PIN configurado no se ejecuta nada
        if (string.IsNullOrEmpty(_pinPeriodo))
        {
            await EnviarSeguroAsync(chatId,
                "⚙️ <b>PIN no configurado.</b>\nDefine la variable <code>Telegram__PinPeriodoAcademico</code> en Render y reinicia el servicio.");
            return;
        }

        if (PinBloqueado(chatId, out var minutos))
        {
            await EnviarSeguroAsync(chatId, $"🔒 Demasiados intentos fallidos. Intenta de nuevo en {minutos} min.");
            return;
        }

        NuevaSesion(chatId, PasoPanel.EsperandoPin);
        await EnviarSeguroAsync(chatId,
            "🔐 <b>Iniciar nuevo periodo académico</b>\n\nEscribe el <b>PIN</b> de seguridad.\n<i>Tu mensaje con el PIN se borrará del chat.</i>",
            BotonesCancelar());
    }

    private bool PinBloqueado(long chatId, out int minutosRestantes)
    {
        minutosRestantes = 0;
        if (!_controlPin.TryGetValue(chatId, out var c) || c.BloqueadoHasta is not { } hasta) return false;

        if (hasta > DateTime.UtcNow)
        {
            minutosRestantes = (int)Math.Ceiling((hasta - DateTime.UtcNow).TotalMinutes);
            return true;
        }

        _controlPin.TryRemove(chatId, out _);
        return false;
    }

    private async Task ValidarPinAsync(long chatId, int messageId, string texto, SesionPanel sesion)
    {
        // El PIN no se queda en el historial del chat
        await BorrarMensajeAsync(chatId, messageId);

        bool correcto = !string.IsNullOrEmpty(_pinPeriodo) &&
                        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(texto), Encoding.UTF8.GetBytes(_pinPeriodo));

        if (correcto)
        {
            _controlPin.TryRemove(chatId, out _);
            sesion.Paso = PasoPanel.EsperandoPdf;
            await EnviarSeguroAsync(chatId,
                "✅ PIN correcto.\n\n📎 Ahora envía el <b>PDF del calendario</b> del nuevo periodo (como archivo).",
                BotonesCancelar());
            return;
        }

        var control = _controlPin.GetOrAdd(chatId, _ => new ControlPin());
        control.Fallos++;

        if (control.Fallos >= MaxIntentosPin)
        {
            control.Fallos = 0;
            control.BloqueadoHasta = DateTime.UtcNow.Add(DuracionBloqueoPin);
            TerminarSesion(chatId, sesion);
            _logger.LogWarning($"[Periodo] PIN incorrecto {MaxIntentosPin} veces desde el chat {chatId}. Panel bloqueado {DuracionBloqueoPin.TotalMinutes} min.");
            await EnviarSeguroAsync(chatId, $"🔒 PIN incorrecto. El panel queda bloqueado por {DuracionBloqueoPin.TotalMinutes} minutos.");
            return;
        }

        await EnviarSeguroAsync(chatId, $"❌ PIN incorrecto. Te quedan {MaxIntentosPin - control.Fallos} intento(s).", BotonesCancelar());
    }

    // ========================================================================
    // 7. FLUJO "INICIAR NUEVO PERIODO" — PDF y resumen (no toca la base de datos)
    // ========================================================================
    private async Task ProcesarDocumentoAsync(long chatId, Document documento)
    {
        var sesion = ObtenerSesion(chatId);
        if (sesion == null || sesion.Paso != PasoPanel.EsperandoPdf)
        {
            await EnviarSeguroAsync(chatId,
                "📎 No estoy esperando ningún archivo.\nPara cargar un calendario usa <b>🎓 Periodo Académico → 🚀 Iniciar nuevo periodo</b>.");
            return;
        }

        var nombre = string.IsNullOrWhiteSpace(documento.FileName) ? "calendario.pdf" : documento.FileName;
        bool esPdf = documento.MimeType == "application/pdf" || nombre.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        if (!esPdf)
        {
            await EnviarSeguroAsync(chatId, "❌ El archivo debe ser un <b>PDF</b>. Envía el calendario en PDF.");
            return;
        }

        if (documento.FileSize is > MaxBytesPdf)
        {
            await EnviarSeguroAsync(chatId, "❌ El PDF pesa más de 20 MB, el máximo que Telegram permite descargar a un bot.");
            return;
        }

        sesion.Paso = PasoPanel.AnalizandoPdf;
        sesion.UltimaActividad = DateTime.UtcNow;
        await EnviarSeguroAsync(chatId, $"⏳ Analizando <b>{Html(nombre)}</b>...");

        _ = Task.Run(async () =>
        {
            try
            {
                using var memoria = new MemoryStream();
                var archivo = await _botClient.GetFile(documento.FileId);
                await _botClient.DownloadFile(archivo.FilePath!, memoria);
                memoria.Position = 0;

                using var scope = _scopeFactory.CreateScope();
                var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();

                var calendario = periodo.ExtraerCalendario(memoria, nombre);

                if (calendario.Evaluaciones.Count == 0)
                {
                    VolverAEsperarPdf(sesion);
                    await EnviarSeguroAsync(chatId,
                        "⚠️ No encontré evaluaciones (TP/TSP) en ese PDF. <b>No se borró nada.</b>\n¿Es el calendario correcto? Envía otro PDF o escribe /cancelar.");
                    return;
                }

                var fueraDeCatalogo = await periodo.ObtenerCodigosFueraDeCatalogoAsync(calendario);
                var totalEstudiantes = await periodo.ContarEstudiantesAsync();
                var resumen = ConstruirResumenPeriodo(calendario, fueraDeCatalogo, totalEstudiantes);

                int validas = calendario.Evaluaciones.Count(e => e.CodigoMateria != null && !fueraDeCatalogo.Contains(e.CodigoMateria));
                if (validas == 0)
                {
                    VolverAEsperarPdf(sesion);
                    await EnviarSeguroAsync(chatId, resumen +
                        "\n\n❌ <b>Ningún código del PDF existe en el catálogo.</b> No se borró nada.\nRevisa el catálogo o envía otro PDF.");
                    return;
                }

                sesion.Calendario = calendario;
                sesion.Paso = PasoPanel.EsperandoConfirmacionPeriodo;
                sesion.UltimaActividad = DateTime.UtcNow;

                await EnviarSeguroAsync(chatId, resumen, BotonesConfirmar("✅ Confirmar reinicio", CbConfirmarPeriodo));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Periodo] Error analizando el PDF del calendario.");
                VolverAEsperarPdf(sesion);
                await EnviarSeguroAsync(chatId,
                    $"❌ No pude leer el PDF: {Html(ex.GetBaseException().Message)}\n<b>No se borró nada.</b> Envía otro PDF o escribe /cancelar.");
            }
        });
    }

    private static void VolverAEsperarPdf(SesionPanel sesion)
    {
        sesion.Calendario = null;
        sesion.Paso = PasoPanel.EsperandoPdf;
        sesion.UltimaActividad = DateTime.UtcNow;
    }

    private static string ConstruirResumenPeriodo(CalendarioProcesado calendario, List<string> fueraDeCatalogo, int totalEstudiantes)
    {
        var evals = calendario.Evaluaciones;
        int materias = evals.Select(e => e.CodigoMateria).Distinct().Count();
        int tp = evals.Count(e => e.Tipo.StartsWith("TP", StringComparison.OrdinalIgnoreCase));
        int tsp = evals.Count(e => e.Tipo.StartsWith("TSP", StringComparison.OrdinalIgnoreCase));
        int omitidas = evals.Count(e => e.CodigoMateria == null || fueraDeCatalogo.Contains(e.CodigoMateria));
        var desde = evals.Min(e => e.FechaEntrega);
        var hasta = evals.Max(e => e.FechaEntrega);

        var sb = new StringBuilder();
        sb.AppendLine("📄 <b>Resumen del calendario</b>");
        sb.AppendLine($"Archivo: <code>{Html(calendario.NombreArchivo)}</code>");
        sb.AppendLine($"Materias: <b>{materias}</b> · Evaluaciones: <b>{evals.Count}</b> (TP {tp} · TSP {tsp})");
        sb.AppendLine($"Fechas: {desde:dd/MM/yyyy} → {hasta:dd/MM/yyyy}");

        if (fueraDeCatalogo.Count > 0)
        {
            const int max = 30;
            var lista = string.Join(", ", fueraDeCatalogo.Take(max)) + (fueraDeCatalogo.Count > max ? "…" : "");
            sb.AppendLine();
            sb.AppendLine($"⚠️ <b>{fueraDeCatalogo.Count} código(s) fuera del catálogo</b> — se omitirán {omitidas} evaluación(es):");
            sb.AppendLine($"<code>{Html(lista)}</code>");
        }

        sb.AppendLine();
        sb.AppendLine("🧹 <b>Al confirmar se hará, todo o nada:</b>");
        sb.AppendLine("• Borrar calendario, evaluaciones y TPs publicados del periodo actual");
        sb.AppendLine($"• Vaciar las materias de {totalEstudiantes} estudiante(s) (se conservan sus datos)");
        sb.AppendLine("• Cargar este calendario");
        sb.AppendLine("Después se envía la convocatoria por correo (por lotes con tope cada 24 h; lo que no quepa sigue sola).");
        sb.AppendLine();
        sb.AppendLine("<i>Esta acción no se puede deshacer.</i>");
        return sb.ToString();
    }

    // ========================================================================
    // 8. FLUJO "INICIAR NUEVO PERIODO" — confirmar → limpiar + cargar → convocatoria
    // ========================================================================
    private async Task ConfirmarPeriodoAsync(long chatId)
    {
        var sesion = ObtenerSesion(chatId);
        CalendarioProcesado? calendario = null;

        // Paso atómico a "Procesando": un doble toque en Confirmar no ejecuta dos veces
        if (sesion != null)
        {
            lock (sesion)
            {
                if (sesion.Paso == PasoPanel.EsperandoConfirmacionPeriodo && sesion.Calendario != null)
                {
                    sesion.Paso = PasoPanel.Procesando;
                    calendario = sesion.Calendario;
                }
            }
        }

        if (sesion == null || calendario == null)
        {
            await EnviarSeguroAsync(chatId, "⌛ Esta confirmación ya no es válida (expiró, se canceló o ya se ejecutó).\nEmpieza de nuevo desde <b>🎓 Periodo Académico</b>.");
            return;
        }

        _logger.LogWarning($"[Periodo] El admin {chatId} confirmó el inicio de un nuevo periodo con '{calendario.NombreArchivo}'.");
        await EnviarSeguroAsync(chatId, "⏳ <b>Reiniciando el semestre y cargando el nuevo calendario...</b>");

        _ = Task.Run(async () =>
        {
            try
            {
                // 1. Limpiar + cargar (transacción única dentro del servicio)
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();
                    var carga = await periodo.ReiniciarYCargarAsync(calendario);
                    await EnviarSeguroAsync(chatId, FormatearResultadoCarga(carga));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Periodo] Error reiniciando y cargando el nuevo periodo.");
                    await EnviarSeguroAsync(chatId,
                        $"❌ <b>No se pudo iniciar el periodo:</b> {Html(ex.GetBaseException().Message)}\n\n<i>La operación se revirtió: el semestre anterior quedó intacto y no se envió ningún correo.</i>");
                    return;
                }

                // 2. Convocatoria masiva (el periodo ya quedó cargado aunque esto falle)
                await EnviarSeguroAsync(chatId, "📣 Enviando la convocatoria a los estudiantes...\n<i>Puede tardar unos minutos.</i>");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();
                    var convocatoria = await periodo.EnviarConvocatoriaAsync();
                    await EnviarSeguroAsync(chatId, FormatearResultadoConvocatoria(convocatoria));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Periodo] Error enviando la convocatoria del nuevo periodo.");
                    await EnviarSeguroAsync(chatId,
                        $"⚠️ <b>El periodo se cargó, pero la convocatoria falló:</b> {Html(ex.GetBaseException().Message)}\n" +
                        "Puedes lanzarla desde <b>🎓 Periodo Académico → 📣 Estado de la convocatoria</b>. Solo se envía a quien no la recibió.");
                }
            }
            finally
            {
                TerminarSesion(chatId, sesion);
            }
        });
    }

    private static string FormatearResultadoCarga(ResultadoCargaPeriodo r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("✅ <b>Nuevo periodo cargado</b>");
        sb.AppendLine($"Calendario #{r.CalendarioId} · {r.EvaluacionesGuardadas} evaluaciones guardadas");
        sb.AppendLine();
        sb.AppendLine($"🧹 Limpieza: {r.Limpieza.CalendariosBorrados} calendario(s), {r.Limpieza.EvaluacionesBorradas} evaluaciones, " +
                      $"{r.Limpieza.TrabajosBorrados} TPs publicados, {r.Limpieza.EstudiantesReseteados} estudiante(s) reiniciados");

        if (r.EvaluacionesOmitidas > 0)
            sb.AppendLine($"⚠️ Omitidas {r.EvaluacionesOmitidas} evaluación(es) de códigos fuera del catálogo: <code>{Html(string.Join(", ", r.CodigosOmitidos))}</code>");

        return sb.ToString();
    }

    public static string FormatearResultadoConvocatoria(ResultadoConvocatoria r, bool esReanudacion = false)
    {
        if (r.Motivo == MotivoFinConvocatoria.YaEnCurso)
            return "⏳ Ya hay una convocatoria enviándose en este momento. Te aviso cuando avance.";

        if (r.Motivo == MotivoFinConvocatoria.NadaPendiente)
            return "ℹ️ No hay estudiantes pendientes de recibir la convocatoria.";

        var sb = new StringBuilder();
        sb.AppendLine(esReanudacion ? "📣 <b>Convocatoria — reanudación automática</b>" : "📣 <b>Convocatoria</b>");
        sb.AppendLine($"Enviados ahora: <b>{r.Exitosos}</b> · Fallidos: {r.Fallidos} · Pendientes: <b>{r.Restantes}</b>");
        sb.AppendLine($"Usados en las últimas 24 h: {r.EnviadosUltimas24h}/{r.LimiteDiario}");
        sb.AppendLine();

        switch (r.Motivo)
        {
            case MotivoFinConvocatoria.Completada:
                sb.AppendLine("✅ <b>Convocatoria completada.</b> Todos los estudiantes la recibieron.");
                break;
            case MotivoFinConvocatoria.TopeDiario:
                sb.AppendLine($"⏸️ Se alcanzó el tope de {r.LimiteDiario} correos en 24 h. <b>Se reanuda sola</b>{TextoProximoCupo(r.ProximoCupoUtc)}.");
                break;
            case MotivoFinConvocatoria.GuardianDiario:
                sb.AppendLine("🛑 El guardián del límite diario de correos cortó el envío. <b>Se reanuda sola</b> cuando haya cupo.");
                break;
            case MotivoFinConvocatoria.CuotaGmail:
                sb.AppendLine("🛑 Gmail indicó que la cuota de envío está agotada. <b>Se reanuda sola</b> más tarde.");
                break;
            case MotivoFinConvocatoria.ErroresSeguidos:
                sb.AppendLine($"⚠️ Se detuvo por errores: {Html(r.Detalle)}");
                sb.AppendLine("Se reintenta sola en la próxima revisión (cada hora).");
                break;
        }

        if (r.Restantes > 0)
            sb.AppendLine("<i>Nadie la recibe dos veces: cada envío queda marcado. La reanudación corre de 7:00 a 22:00.</i>");

        return sb.ToString();
    }

    private static string TextoProximoCupo(DateTime? utc) =>
        utc is { } t ? $" a partir de ~{t.AddHours(-4):dd/MM HH:mm} (hora de Venezuela)" : "";

    // ---- Estado y envío manual de la convocatoria (sin PIN: solo envía a pendientes, con tope) ----
    private async Task MostrarEstadoConvocatoriaAsync(long chatId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();
            var estado = await periodo.ObtenerEstadoConvocatoriaAsync();
            var reanudacionActiva = await periodo.HayConvocatoriaEnCursoAsync();

            var sb = new StringBuilder();
            sb.AppendLine("📣 <b>Estado de la convocatoria</b>");
            sb.AppendLine($"Pendientes: <b>{estado.Pendientes}</b>");
            sb.AppendLine($"Enviados en las últimas 24 h: {estado.EnviadosUltimas24h}/{estado.LimiteDiario}");
            sb.AppendLine($"Cupo disponible ahora: <b>{estado.CupoDisponible}</b>");

            if (estado.CupoDisponible == 0 && estado.ProximoCupoUtc != null)
                sb.AppendLine($"Próximo cupo{TextoProximoCupo(estado.ProximoCupoUtc)}");

            sb.AppendLine();
            if (estado.EnCurso) sb.AppendLine("⏳ Se está enviando en este momento.");
            else if (estado.Pendientes == 0) sb.AppendLine("✅ No hay nadie pendiente.");
            else if (reanudacionActiva) sb.AppendLine("🔁 Reanudación automática activa (revisa cada hora, de 7:00 a 22:00).");
            else sb.AppendLine("ℹ️ No hay una convocatoria en marcha. Puedes enviarla a los pendientes ahora.");

            bool puedeEnviar = !estado.EnCurso && estado.Pendientes > 0 && estado.CupoDisponible > 0;
            var botones = puedeEnviar
                ? BotonesConfirmar($"📣 Enviar ahora (hasta {Math.Min(estado.Pendientes, estado.CupoDisponible)})", CbEnviarConvocatoria)
                : null;

            await EnviarSeguroAsync(chatId, sb.ToString(), botones);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Telegram] Error consultando el estado de la convocatoria.");
            await EnviarSeguroAsync(chatId, $"❌ No pude consultar la convocatoria: {Html(ex.GetBaseException().Message)}");
        }
    }

    private async Task EnviarConvocatoriaManualAsync(long chatId)
    {
        await EnviarSeguroAsync(chatId, "📣 Enviando la convocatoria a los pendientes...\n<i>Puede tardar unos minutos.</i>");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var periodo = scope.ServiceProvider.GetRequiredService<PeriodoAcademicoService>();
                var resultado = await periodo.EnviarConvocatoriaAsync();
                await EnviarSeguroAsync(chatId, FormatearResultadoConvocatoria(resultado));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Telegram] Error enviando la convocatoria manual.");
                await EnviarSeguroAsync(chatId, $"❌ <b>Error enviando la convocatoria:</b> {Html(ex.GetBaseException().Message)}");
            }
        });
    }

    /// <summary>Mensaje a todos los administradores (lo usa el ConvocatoriaWorkerService).</summary>
    public async Task NotificarAdminsAsync(string html)
    {
        foreach (var adminId in _adminChatIds)
            await EnviarSeguroAsync(adminId, html);
    }

    // ========================================================================
    // 9. FLUJO "GENERAR PLAN A ESTUDIANTE"
    // ========================================================================
    private async Task ConfirmarPlanAsync(long chatId)
    {
        var sesion = ObtenerSesion(chatId);
        bool listo = false;

        if (sesion != null)
        {
            lock (sesion)
            {
                if (sesion.Paso == PasoPanel.EsperandoConfirmacionPlan)
                {
                    sesion.Paso = PasoPanel.Procesando;
                    listo = true;
                }
            }
        }

        if (sesion == null || !listo)
        {
            await EnviarSeguroAsync(chatId, "⌛ Esta confirmación ya no es válida. Empieza de nuevo desde <b>🎓 Periodo Académico</b>.");
            return;
        }

        string nombre = sesion.Nombre, correo = sesion.Correo, codigos = sesion.Codigos;
        await EnviarSeguroAsync(chatId, "⏳ Generando el plan y enviando el correo...");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var planService = scope.ServiceProvider.GetRequiredService<PlanEstudianteService>();
                var r = await planService.GenerarYEnviarAsync(nombre, correo, codigos);

                await EnviarSeguroAsync(chatId, FormatearResultadoPlan(r, correo));
                _logger.LogInformation($"[Telegram] Plan generado desde el panel para {correo}: {r.Estado}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Telegram] Error generando el plan desde el panel.");
                await EnviarSeguroAsync(chatId, $"❌ <b>Error generando el plan:</b> {Html(ex.GetBaseException().Message)}");
            }
            finally
            {
                TerminarSesion(chatId, sesion);
            }
        });
    }

    private static string FormatearResultadoPlan(ResultadoPlanEstudiante r, string correo)
    {
        var sb = new StringBuilder();

        if (r.Estado != EstadoPlanEstudiante.Ok)
        {
            sb.AppendLine($"❌ <b>No se envió el plan:</b> {Html(r.Error)}");
            if (r.MateriasNoEncontradas.Count > 0)
                sb.AppendLine($"Códigos no encontrados: <code>{Html(string.Join(", ", r.MateriasNoEncontradas))}</code>");
            return sb.ToString();
        }

        sb.AppendLine($"✅ <b>Plan enviado</b> a <code>{Html(correo)}</code>");
        sb.AppendLine($"Materias: <code>{Html(string.Join(", ", r.MateriasSolicitadas))}</code>");

        if (r.MateriasNoEncontradas.Count > 0)
            sb.AppendLine($"⚠️ No están en el catálogo: <code>{Html(string.Join(", ", r.MateriasNoEncontradas))}</code>");

        if (r.Suscripcion != null)
        {
            sb.AppendLine(r.Suscripcion.EsNuevo ? "🆕 Estudiante suscrito a las alertas" : "🔄 Suscripción actualizada");
            sb.AppendLine($"📬 Trabajos ya publicados enviados: {r.Suscripcion.TrabajosEnviados}");
            if (r.Suscripcion.ErrorEnvioTrabajos != null)
                sb.AppendLine($"⚠️ Falló el correo de trabajos publicados: {Html(r.Suscripcion.ErrorEnvioTrabajos)}");
        }

        return sb.ToString();
    }

    // ========================================================================
    // 10. HELPERS DE TELEGRAM
    // ========================================================================
    private static InlineKeyboardMarkup BotonesCancelar() =>
        new(new[] { new[] { InlineKeyboardButton.WithCallbackData("❌ Cancelar", CbCancelar) } });

    private static InlineKeyboardMarkup BotonesConfirmar(string textoConfirmar, string callbackConfirmar) =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(textoConfirmar, callbackConfirmar),
                InlineKeyboardButton.WithCallbackData("❌ Cancelar", CbCancelar)
            }
        });

    private async Task ResponderCallbackAsync(string callbackId, string? texto = null)
    {
        try { await _botClient.AnswerCallbackQuery(callbackId, texto); }
        catch (Exception ex) { _logger.LogInformation($"[Telegram] No se pudo responder el callback: {ex.Message}"); }
    }

    private async Task QuitarBotonesAsync(long chatId, int messageId)
    {
        try { await _botClient.EditMessageReplyMarkup(chatId, messageId, replyMarkup: null); }
        catch (Exception ex) { _logger.LogInformation($"[Telegram] No se pudieron quitar los botones: {ex.Message}"); }
    }

    private async Task BorrarMensajeAsync(long chatId, int messageId)
    {
        try { await _botClient.DeleteMessage(chatId, messageId); }
        catch (Exception ex) { _logger.LogInformation($"[Telegram] No se pudo borrar el mensaje: {ex.Message}"); }
    }

    private static string Html(string? texto) => System.Net.WebUtility.HtmlEncode(texto ?? "");

    // Envía HTML. Sin teclado inline explícito, re-adjunta el teclado inferior para que no se pierda.
    private async Task EnviarSeguroAsync(long chatId, string html, InlineKeyboardMarkup? tecladoInline = null)
    {
        try
        {
            if (tecladoInline != null)
            {
                await _botClient.SendMessage(chatId: chatId, text: html, parseMode: ParseMode.Html, replyMarkup: tecladoInline);
            }
            else
            {
                await _botClient.SendMessage(chatId: chatId, text: html, parseMode: ParseMode.Html, replyMarkup: CrearTecladoDinamico());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error enviando mensaje a Telegram: {ex.Message}");
        }
    }

    // Constructor del teclado persistente en la parte inferior
    private ReplyKeyboardMarkup CrearTecladoDinamico()
    {
        string textoBoton = ModoEnVivoActivado ? BotonApagarLogs : BotonEncenderLogs;

        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { textoBoton },
            new KeyboardButton[] { BotonBuscarTps },
            new KeyboardButton[] { BotonPeriodoAcademico }
        })
        {
            ResizeKeyboard = true, // Hace que el teclado se ajuste bonito y no ocupe media pantalla
            IsPersistent = true    // Le dice a Telegram que mantenga este teclado siempre abierto
        };
    }
}
