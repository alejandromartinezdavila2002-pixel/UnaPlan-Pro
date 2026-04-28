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
using Microsoft.EntityFrameworkCore;

// Importa tus namespaces reales aquí
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

    public NotionWorkerService(
        ILogger<NotionWorkerService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;

        // Inicializamos el cliente de Notion con el Token secreto
        _notionClient = NotionClientFactory.Create(new ClientOptions
        {
            AuthToken = _config["NotionSettings:Token"]
        });
        _databaseId = _config["NotionSettings:DatabaseId"]!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Notion Worker Iniciado y vigilando la tabla...");

        // Este es el ciclo infinito que reemplaza a Make.com
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcesarSolicitudesPendientesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error en el ciclo del Worker: {ex.Message}");
            }

            // El Worker "duerme" por 30 segundos antes de volver a preguntar a Notion
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcesarSolicitudesPendientesAsync()
    {
        // 1. Preguntamos a Notion: "¿Hay alguien con Estado = Pendiente?"
        // Buscamos todas las filas donde la columna "Estado" esté VACÍA
        var queryParams = new DatabasesQueryParameters
        {
            Filter = new SelectFilter("Estado", isEmpty: true)
        };

        var paginatedList = await _notionClient.Databases.QueryAsync(_databaseId, queryParams);

        if (!paginatedList.Results.Any())
            return; // Si no hay nadie, terminamos y vuelve a dormir

        _logger.LogInformation($"📥 Se encontraron {paginatedList.Results.Count} solicitudes nuevas.");

        // 2. Creamos un Scope (Un espacio de trabajo temporal para inyectar servicios)
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var excelService = scope.ServiceProvider.GetRequiredService<ExcelGeneratorService>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        // 3. Procesamos cada fila encontrada
        // 3. Procesamos cada fila encontrada
        foreach (var resultado in paginatedList.Results)
        {
            // MAGIA DE ARQUITECTO: Casteo seguro (Pattern Matching)
            // Obligamos a C# a tratar el objeto estrictamente como una página de Notion
            if (resultado is Notion.Client.Page page)
            {
                try
                {
                    // ¡Adiós al error de Properties! Ahora 'page' tiene acceso total a sus propiedades
                    string nombre = ObtenerTextoDePropiedad(page.Properties, "Nombre Completo", true);
                    string correo = ObtenerTextoDePropiedad(page.Properties, "Correo", false);
                    string codigosMaterias = ObtenerTextoDePropiedad(page.Properties, "Materias", false);

                    _logger.LogInformation($"Procesando a: {nombre} ({correo}) - Materias: {codigosMaterias}");

                    // ---LÓGICA DE PROGRAM.CS VIENE AQUÍ ---
                    char[] separadores = { ',', '.', ' ', '-', ';' };
                    List<string> listaMaterias = codigosMaterias
                        .Split(separadores, StringSplitOptions.RemoveEmptyEntries)
                        .Select(m => m.Trim())
                        .Distinct()
                        .ToList();

                    var materiasBd = await db.PlanesDeCurso
                     .Include(p => p.MaterialesDeApoyo)
                     .Include(p => p.Evaluaciones)
                     .Where(p => listaMaterias.Contains(p.CodigoMateria))
                     .AsSplitQuery() 
                     .ToListAsync();

                    var codigosEncontrados = materiasBd.Select(m => m.CodigoMateria).ToList();
                    List<string> materiasNoEncontradas = listaMaterias.Except(codigosEncontrados).ToList();

                    var listaParaExcel = new List<ExcelGeneratorService.MateriaPlanillaDto>();

                    foreach (var materia in materiasBd)
                    {
                        if (materia.Evaluaciones != null && materia.Evaluaciones.Any())
                        {
                            foreach (var eval in materia.Evaluaciones)
                            {
                                listaParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                                {
                                    Codigo = materia.CodigoMateria,
                                    Nombre = eval.NombreMateria,
                                    TipoEvaluacion = eval.Tipo,
                                    FechaEntrega = eval.FechaEntrega.ToString("dd/MM/yyyy"),
                                    UrlPlan = materia.UrlDocumento,
                                    UrlsMateriales = materia.MaterialesDeApoyo.Select(m => m.UrlDrive).ToList()
                                });
                            }
                        }
                        else
                        {
                            listaParaExcel.Add(new ExcelGeneratorService.MateriaPlanillaDto
                            {
                                Codigo = materia.CodigoMateria,
                                Nombre = materia.NombreMateria,
                                TipoEvaluacion = "Pendiente por definir",
                                FechaEntrega = "Pendiente",
                                UrlPlan = materia.UrlDocumento,
                                UrlsMateriales = materia.MaterialesDeApoyo.Select(m => m.UrlDrive).ToList()
                            });
                        }
                    }

                    // Generamos y Enviamos
                    var archivoExcelBytes = excelService.GenerarPlanDeEvaluacionExcel(listaParaExcel);
                    await emailService.EnviarPlanPersonalizadoAsync(correo, nombre, archivoExcelBytes, materiasNoEncontradas);

                    // NUEVO: GUARDAR AL ESTUDIANTE PARA ALERTAS FUTURAS (Módulo 5)
                    var estudianteDb = await db.EstudiantesSuscritos.FirstOrDefaultAsync(e => e.Correo == correo);

                    if (estudianteDb == null)
                    {
                        // Es un estudiante nuevo, lo registramos con sus materias
                        db.EstudiantesSuscritos.Add(new EstudiantesSuscritos
                        {
                            Nombre = nombre,
                            Correo = correo,
                            MateriasInscritas = listaMaterias,
                            FechaSuscripcion = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        // Si ya existía, unimos las materias nuevas con las que ya tenía (evitando duplicados)
                        estudianteDb.MateriasInscritas = estudianteDb.MateriasInscritas.Union(listaMaterias).ToList();
                        db.EstudiantesSuscritos.Update(estudianteDb);
                    }
                    await db.SaveChangesAsync();
                    // =======================================================================

                    // --- CIERRE DEL CICLO: Actualizamos el Estado en Notion a "Enviado" ---
                    var updateProps = new Dictionary<string, PropertyValue>
                    {
                        { "Estado", new SelectPropertyValue { Select = new SelectOption { Name = "Enviado" } } }
                    };




                    await _notionClient.Pages.UpdatePropertiesAsync(page.Id, updateProps);
                    _logger.LogInformation($"✅ Solicitud de {nombre} completada y actualizada en Notion.");

                    await Task.Delay(400);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error procesando solicitud ID {page.Id}: {ex.Message}");
                }
            }
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
