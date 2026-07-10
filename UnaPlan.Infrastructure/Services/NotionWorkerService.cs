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

    // 🤖 NUEVO: Inyectamos el Dispatcher que maneja la cola Anti-Spam de Telegram
    private readonly TelegramLogDispatcher _logDispatcher;

    public NotionWorkerService(
        ILogger<NotionWorkerService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        TelegramLogDispatcher logDispatcher) // ⬅️ Agregado al constructor
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _logDispatcher = logDispatcher; // ⬅️ Guardamos la referencia

        // Inicializamos el cliente de Notion con el Token secreto
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
        // 1. Consultar a Notion por filas que tengan Estado vacío (Sin procesar)
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

            foreach (var page in response.Results.OfType<Page>())
            {
                try
                {
                    // 2. Extraer los datos de la fila de Notion usando nuestro método auxiliar
                    string nombre = ObtenerTextoDePropiedad(page.Properties, "Nombre", esTitulo: true);
                    string correo = ObtenerTextoDePropiedad(page.Properties, "Correo", esTitulo: false);
                    string materiasRaw = ObtenerTextoDePropiedad(page.Properties, "Materias", esTitulo: false);

                    _logger.LogInformation($"Procesando a: {nombre} ({correo}) - Materias: {materiasRaw}");

                    if (string.IsNullOrEmpty(correo) || string.IsNullOrEmpty(materiasRaw))
                    {
                        throw new Exception("El correo o las materias están vacías en Notion.");
                    }

                    // 3. Procesar las materias (separadas por coma o espacio)
                    var listaMaterias = materiasRaw
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(m => m.Trim())
                        .ToList();

                    // 4. Buscar en la Base de Datos los planes, materiales y evaluaciones de esas materias
                    var planes = await db.PlanesDeCurso
                        .Where(p => listaMaterias.Contains(p.CodigoMateria))
                        .ToListAsync();

                    var materiales = await db.MaterialesDeApoyo
                        .Where(m => listaMaterias.Contains(m.CodigoMateria))
                        .ToListAsync();

                    var evaluaciones = await db.Evaluaciones
                        .Where(e => listaMaterias.Contains(e.CodigoMateria))
                        .ToListAsync();

                    // 5. Mapear datos para el Excel
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

                    // 6. Generar el archivo Excel en memoria RAM
                    byte[] excelBytes = excelService.GenerarPlanDeEvaluacionExcel(materiasParaExcel);

                    // 7. Despachar el Correo Electrónico (El EmailService maneja internamente el asunto y cuerpo)
                    await emailService.EnviarPlanPersonalizadoAsync(correo, nombre, excelBytes, materiasFaltantes);

                    // 7. Guardar o actualizar al estudiante en nuestra tabla "EstudiantesSuscritos" de Supabase
                    var estudianteExistente = await db.EstudiantesSuscritos
                        .FirstOrDefaultAsync(e => e.Correo == correo);

                    if (estudianteExistente != null)
                    {
                        estudianteExistente.Nombre = nombre;
                        estudianteExistente.MateriasInscritas = listaMaterias;
                        estudianteExistente.FechaSuscripcion = DateTime.UtcNow;
                        db.EstudiantesSuscritos.Update(estudianteExistente);
                    }
                    else
                    {
                        var nuevoEstudiante = new EstudiantesSuscritos
                        {
                            Nombre = nombre,
                            Correo = correo,
                            MateriasInscritas = listaMaterias,
                            FechaSuscripcion = DateTime.UtcNow
                        };
                        await db.EstudiantesSuscritos.AddAsync(nuevoEstudiante);
                    }

                    await db.SaveChangesAsync();

                    // --- CIERRE DEL CICLO: Actualizamos el Estado en Notion a "Enviado" ---
                    var updateProps = new Dictionary<string, PropertyValue>
                    {
                        { "Estado", new SelectPropertyValue { Select = new SelectOption { Name = "Enviado" } } }
                    };

                    await _notionClient.Pages.UpdatePropertiesAsync(page.Id, updateProps);
                    _logger.LogInformation($"✅ Solicitud de {nombre} completada y actualizada en Notion.");

                    // 🔥 NUEVO: Si el Modo en Vivo está encendido, mandamos este log limpio a la cola de Telegram
                    _logDispatcher.EnqueueLog($"✅ {nombre} - Solicitud enviada.");

                    batchDetails.AppendLine($"✅ {nombre} - Solicitud enviada.");
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

        // --- ENVIAR LOTE A TELEGRAM SI HUBO ACTIVIDAD ---
        if (procesadosConExito > 0 || procesadosConError > 0)
        {
            string summary = $"📋 {procesadosConExito} solicitudes procesadas en Notion. ({procesadosConError} errores)";
            UnaPlan.Infrastructure.Logging.TelegramLoggerExtensions.LogTelegramBatch(_logger, summary, batchDetails.ToString());
        }
    }

    // Método auxiliar para leer los diferentes tipos de celdas en Notion
    private string ObtenerTextoDePropiedad(IDictionary<string, PropertyValue> propiedades, string nombreColumna, bool esTitulo)
    {
        if (!propiedades.ContainsKey(nombreColumna)) return "";

        var prop = propiedades[nombreColumna];

        if (esTitulo && prop is TitlePropertyValue titulo)
            return titulo.Title.FirstOrDefault()?.PlainText ?? "";

        if (prop is RichTextPropertyValue textoRico)
            return textoRico.RichText.FirstOrDefault()?.PlainText ?? "";

        if (prop is EmailPropertyValue email)
            return email.Email ?? "";

        return "";
    }
}