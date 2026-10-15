using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Notion.Client;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Microsoft.EntityFrameworkCore;

using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public class NotionWorkerService : BackgroundService
{
    private readonly ILogger<NotionWorkerService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotionClient _notionClient;
    private readonly string _databaseId;

    // Cola anti-spam de Telegram
    private readonly TelegramLogDispatcher _logDispatcher;

    public NotionWorkerService(
        ILogger<NotionWorkerService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        TelegramLogDispatcher logDispatcher)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _logDispatcher = logDispatcher;

        _notionClient = NotionClientFactory.Create(new ClientOptions
        {
            AuthToken = _config["NotionSettings:Token"]
        });
        _databaseId = _config["NotionSettings:DatabaseId"]!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🕵️‍♂️ Vigilante de Notion Activado (Escaneando base de datos del CRM)...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await ProcesarSolicitudesPendientesAsync(db);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error en el ciclo del Worker de Notion: {ex.Message}");
            }

            // El vigilante descansa 30 segundos antes de volver a escanear Notion
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcesarSolicitudesPendientesAsync(AppDbContext db)
    {
        // Filas de Notion con el Estado vacío (sin procesar)
        var queryParams = new DatabasesQueryParameters
        {
            Filter = new CompoundFilter(
                and: new List<Filter>
                {
                    new SelectFilter("Estado", isEmpty: true)
                }
            )
        };

        var response = await _notionClient.Databases.QueryAsync(_databaseId, queryParams);

        if (response.Results.Count == 0)
        {
            return; // No hay trabajo, salimos pacíficamente
        }

        _logger.LogInformation($"📥 Se encontraron {response.Results.Count} solicitudes nuevas.");

        int procesadosConExito = 0;
        int procesadosConError = 0;
        StringBuilder batchDetails = new StringBuilder();

        using (var scope = _scopeFactory.CreateScope())
        {
            var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
            var excelService = scope.ServiceProvider.GetRequiredService<ExcelGeneratorService>();
            var suscripcionService = scope.ServiceProvider.GetRequiredService<SuscripcionService>();

            foreach (var page in response.Results.OfType<Page>())
            {
                try
                {
                    string nombre = ObtenerTextoDePropiedad(page.Properties, "Nombre Completo");
                    string correo = ObtenerTextoDePropiedad(page.Properties, "Correo");
                    string materiasRaw = ObtenerTextoDePropiedad(page.Properties, "Materias");

                    _logger.LogInformation($"Procesando a: {nombre} ({correo}) - Materias: {materiasRaw}");

                    if (string.IsNullOrEmpty(correo) || string.IsNullOrEmpty(materiasRaw))
                    {
                        throw new Exception("El correo o las materias están vacías en Notion.");
                    }

                    var listaMaterias = SuscripcionService.ParsearMaterias(materiasRaw);

                    var planes = await db.PlanesDeCurso
                        .Where(p => listaMaterias.Contains(p.CodigoMateria))
                        .ToListAsync();

                    var materiales = await db.MaterialesDeApoyo
                        .Where(m => listaMaterias.Contains(m.CodigoMateria))
                        .ToListAsync();

                    var evaluaciones = await db.Evaluaciones
                        .Where(e => listaMaterias.Contains(e.CodigoMateria))
                        .ToListAsync();

                    var materiasParaExcel = new List<ExcelGeneratorService.MateriaPlanillaDto>();
                    var materiasEncontradas = planes.Select(p => p.CodigoMateria).Distinct().ToList();
                    var materiasFaltantes = listaMaterias.Except(materiasEncontradas).ToList();

                    foreach (var materia in listaMaterias)
                    {
                        var plan = planes.FirstOrDefault(p => p.CodigoMateria == materia);
                        var mats = materiales.Where(m => m.CodigoMateria == materia).Select(m => m.UrlDrive).ToList();
                        var evals = evaluaciones.Where(e => e.CodigoMateria == materia).ToList();
                        
                        if (evals.Any())
                        {
                            foreach (var eval in evals)
                            {
                                materiasParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                                {
                                    Codigo = materia,
                                    Nombre = plan?.NombreMateria ?? "Sin nombre",
                                    TipoEvaluacion = eval.Tipo,
                                    FechaEntrega = eval.FechaEntrega.ToString("dd/MM/yyyy"),
                                    UrlPlan = plan?.UrlDocumento ?? "",
                                    UrlsMateriales = mats
                                });
                            }
                        }
                        else if (plan != null)
                        {
                            // Si hay plan pero no hay evaluaciones cargadas en BD, al menos enviamos la fila con el plan
                            materiasParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                            {
                                Codigo = materia,
                                Nombre = plan.NombreMateria,
                                TipoEvaluacion = "Sin especificar",
                                FechaEntrega = "Pendiente",
                                UrlPlan = plan.UrlDocumento ?? "",
                                UrlsMateriales = mats
                            });
                        }
                    }

                    byte[] excelBytes = excelService.GenerarPlanDeEvaluacionExcel(materiasParaExcel);

                    await emailService.EnviarPlanPersonalizadoAsync(correo, nombre, excelBytes, materiasFaltantes);

                    // Suscribe al estudiante (y le manda los trabajos ya publicados de sus materias)
                    var suscripcion = await suscripcionService.SuscribirAsync(nombre, correo, listaMaterias);

                    // Marcamos el estado en Notion como "Enviado"
                    var updateProps = new Dictionary<string, PropertyValue>
                    {
                        { "Estado", new SelectPropertyValue { Select = new SelectOption { Name = "Enviado" } } }
                    };

                    await _notionClient.Pages.UpdatePropertiesAsync(page.Id, updateProps);
                    _logger.LogInformation($"✅ Solicitud de {nombre} completada y actualizada en Notion.");

                    // Preparamos el mensaje de éxito para Telegram, con alerta si faltaron materias
                    string mensajeExito = $"✅ {nombre} - Solicitud enviada.";
                    if (materiasFaltantes.Any())
                    {
                        mensajeExito += $" (⚠️ Faltaron: {string.Join(", ", materiasFaltantes)})";
                    }
                    if (suscripcion.TrabajosEnviados > 0)
                    {
                        mensajeExito += $" 📚 +{suscripcion.TrabajosEnviados} trabajos ya publicados.";
                    }
                    if (suscripcion.ErrorEnvioTrabajos != null)
                    {
                        mensajeExito += $" ⚠️ Falló el correo de trabajos publicados: {suscripcion.ErrorEnvioTrabajos}";
                    }

                    _logDispatcher.EnqueueLog(mensajeExito);

                    batchDetails.AppendLine(mensajeExito);
                    procesadosConExito++;
                    await Task.Delay(400);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error procesando solicitud ID {page.Id}: {ex.Message}");
                    batchDetails.AppendLine($"❌ Error en ID {page.Id}: {ex.Message}");
                    procesadosConError++;
                }
            }
        }

        // Si hubo actividad, mandamos el resumen del lote a Telegram
        if (procesadosConExito > 0 || procesadosConError > 0)
        {
            string summary = $"📋 {procesadosConExito} solicitudes procesadas en Notion. ({procesadosConError} errores)";
            UnaPlan.Infrastructure.Logging.TelegramLoggerExtensions.LogTelegramBatch(_logger, summary, batchDetails.ToString());
        }
    }

    // Método auxiliar para leer los diferentes tipos de celdas en Notion de forma automática
    private string ObtenerTextoDePropiedad(IDictionary<string, PropertyValue> propiedades, string nombreColumna)
    {
        // Revisamos si la columna existe (ignorando mayúsculas/minúsculas o espacios extra por seguridad)
        var key = propiedades.Keys.FirstOrDefault(k => k.Trim().Equals(nombreColumna, StringComparison.OrdinalIgnoreCase));
        if (key == null) return "";

        var prop = propiedades[key];

        if (prop is TitlePropertyValue titulo && titulo.Title != null)
            return string.Join("", titulo.Title.Select(t => t.PlainText));

        if (prop is RichTextPropertyValue textoRico && textoRico.RichText != null)
            return string.Join("", textoRico.RichText.Select(t => t.PlainText));

        if (prop is EmailPropertyValue email)
            return email.Email ?? "";

        return "";
    }
}