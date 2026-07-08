using Microsoft.EntityFrameworkCore;
using Notion.Client;
using UnaPlan.Infrastructure.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System;

namespace UnaPlan.Infrastructure.Services;

public class NotionPublisherService : BackgroundService
{
    private readonly INotionClient _notionClient;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotionPublisherService> _logger;

    // Memoria para evitar publicaciones duplicadas el mismo lunes
    private int _ultimoDiaRevisado = -1;
    private bool _yaSePublicoEstaSemana = false;

    public NotionPublisherService(INotionClient notionClient, IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<NotionPublisherService> logger)
    {
        _notionClient = notionClient;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🤖 Motor Automático de Notion Activado (Patrullaje de Lunes).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);

                // 1. Reseteo semanal: Los domingos preparamos el sistema para el día siguiente
                if (veTime.DayOfWeek == DayOfWeek.Sunday && veTime.Day != _ultimoDiaRevisado)
                {
                    _ultimoDiaRevisado = veTime.Day;
                    _yaSePublicoEstaSemana = false;
                    _logger.LogInformation("[Notion-Worker] 🔄 Reseteo dominical completado. Listo para publicar mañana.");
                }

                // 2. Ahorro de recursos extremo: Si no es Lunes, dormimos por 12 horas
                if (veTime.DayOfWeek != DayOfWeek.Monday)
                {
                    await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
                    continue;
                }

                // 3. Ejecución del Lunes (A partir de las 6:00 AM)
                if (veTime.DayOfWeek == DayOfWeek.Monday && veTime.Hour >= 6 && !_yaSePublicoEstaSemana)
                {
                    _logger.LogInformation("[Notion-Worker] 🚀 ¡Es Lunes! Iniciando sincronización automática de la cartelera...");

                    // Disparamos la lógica interna de sincronización
                    await SincronizarCarteleraAsync();

                    _yaSePublicoEstaSemana = true;
                    _ultimoDiaRevisado = veTime.Day;
                    _logger.LogInformation("[Notion-Worker] ✅ Cartelera sincronizada. El motor se apaga hasta el próximo lunes.");
                }

                // Si es Lunes pero aún no son las 6:00 AM, verificamos cada hora (muy ligero)
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error crítico en el Motor Automático de Notion.");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }

    public async Task SincronizarCarteleraAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var databaseId = _config["NotionSettings:CarteleraDatabaseId"];

        if (string.IsNullOrEmpty(databaseId))
        {
            _logger.LogError("El ID de la Cartelera de Notion no está configurado.");
            return;
        }

        try
        {
            // 1. Calcular exactamente la fecha del próximo sábado
            DateTime hoy = DateTime.UtcNow.Date;
            int diasHastaSabado = ((int)DayOfWeek.Saturday - (int)hoy.DayOfWeek + 7) % 7;

            // Si hoy es sábado, asume hoy mismo. Si prefieres que busque el de la otra semana, cambiaríamos esto a 7.
            if (diasHastaSabado == 0) diasHastaSabado = 7;

            DateTime proximoSabado = hoy.AddDays(diasHastaSabado);

            _logger.LogInformation($"Buscando evaluaciones exclusivamente para el próximo sábado: {proximoSabado:dd/MM/yyyy}");

            // 2. Filtrar la base de datos priorizando FechaEntregaReal
            var evaluaciones = await db.Evaluaciones
                .Where(e =>
                    (e.FechaEntregaReal.HasValue && e.FechaEntregaReal.Value.Date == proximoSabado) ||
                    (!e.FechaEntregaReal.HasValue && e.FechaEntrega.Date == proximoSabado))
                .ToListAsync();

            _logger.LogInformation($"Se encontraron {evaluaciones.Count} evaluaciones para publicar.");

            // 3. Escribir cada evaluación en la Cartelera de Notion
            foreach (var eval in evaluaciones)
            {
                var tipoEvaluacion = eval.Tipo ?? "Por definir";
                var materiaNombre = eval.NombreMateria ?? "Materia sin nombre";
                var codigoMateria = eval.CodigoMateria ?? "N/A";

                // Determinamos la fecha final a imprimir
                var fechaAMostrar = eval.FechaEntregaReal ?? eval.FechaEntrega;

                var properties = new Dictionary<string, PropertyValue>
                {
                    { "Materia", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextText { Text = new Text { Content = materiaNombre } } } } },
                    { "Código", new RichTextPropertyValue { RichText = new List<RichTextBase> { new RichTextText { Text = new Text { Content = codigoMateria } } } } },
                    { "Tipo", new SelectPropertyValue { Select = new SelectOption { Name = tipoEvaluacion } } },
                    { "Fecha de Entrega o Publicación", new DatePropertyValue { Date = new Date { Start = fechaAMostrar.Date.AddHours(12) } } },
                    { "Semana", new NumberPropertyValue { Number = eval.Semana ?? 0 } }
                };

                var pageContent = new List<IBlock>
                {
                    new ParagraphBlock
                    {
                        Paragraph = new ParagraphBlock.Info
                        {
                            RichText = new List<RichTextBase>
                            {
                                new RichTextText
                                {
                                    Text = new Text { Content = "Detalles de la Evaluación" },
                                    Annotations = new Annotations { IsBold = true }
                                }
                            }
                        }
                    },
                    new ParagraphBlock
                    {
                        Paragraph = new ParagraphBlock.Info
                        {
                            RichText = new List<RichTextBase>
                            {
                                new RichTextText { Text = new Text { Content = $"Esta entrega corresponde a un trabajo de tipo: {tipoEvaluacion} (TSP/TP)." } }
                            }
                        }
                    }
                };

                await _notionClient.Pages.CreateAsync(new PagesCreateParameters
                {
                    Parent = new DatabaseParentInput { DatabaseId = databaseId },
                    Properties = properties,
                    Children = pageContent
                });

                _logger.LogInformation($"Publicado: {materiaNombre} ({codigoMateria}) - {tipoEvaluacion} para el {fechaAMostrar:dd/MM/yyyy}");

                await Task.Delay(400);
            }

            _logger.LogInformation("✅ Sincronización de la cartelera completada.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Error sincronizando la cartelera: {ex.Message}");
        }
    }
}
