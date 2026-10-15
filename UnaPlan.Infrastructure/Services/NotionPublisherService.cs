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
    private readonly TelegramLogDispatcher _logDispatcher;

    // Memoria para evitar publicaciones duplicadas el mismo lunes
    private int _ultimoDiaRevisado = -1;
    private bool _yaSePublicoEstaSemana = false;

    // Si la Cartelera activa llega a esta cantidad de entradas, avisamos por Telegram
    // antes de que Notion agote el cupo de bloques gratuito del workspace.
    private const int UmbralAlertaBloques = 150;

    public NotionPublisherService(INotionClient notionClient, IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<NotionPublisherService> logger, TelegramLogDispatcher logDispatcher)
    {
        _notionClient = notionClient;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _logDispatcher = logDispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🤖 Motor Automático de Notion Activado (Patrullaje de Lunes).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);

                // Los domingos dejamos todo listo para publicar al día siguiente
                if (veTime.DayOfWeek == DayOfWeek.Sunday && veTime.Day != _ultimoDiaRevisado)
                {
                    _ultimoDiaRevisado = veTime.Day;
                    _yaSePublicoEstaSemana = false;
                    _logger.LogInformation("[Notion-Worker] 🔄 Reseteo dominical completado. Listo para publicar mañana.");
                }

                // Si no es lunes no hay nada que hacer, dormimos 12 horas
                if (veTime.DayOfWeek != DayOfWeek.Monday)
                {
                    await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
                    continue;
                }

                // Lunes a partir de las 6:00 AM
                if (veTime.DayOfWeek == DayOfWeek.Monday && veTime.Hour >= 6 && !_yaSePublicoEstaSemana)
                {
                    _logger.LogInformation("[Notion-Worker] 🚀 ¡Es Lunes! Iniciando sincronización automática de la cartelera...");

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
            // Antes de publicar lo nuevo, archivamos en Notion lo que ya pasó. Así la tabla activa
            // no crece para siempre y no volvemos a agotar el cupo de bloques del plan gratuito.
            await LimpiarEntradasPasadasAsync(databaseId);

            DateTime hoy = DateTime.UtcNow.Date;
            int diasHastaSabado = ((int)DayOfWeek.Saturday - (int)hoy.DayOfWeek + 7) % 7;

            // Si hoy es sábado, asume hoy mismo. Si prefieres que busque el de la otra semana, cambiaríamos esto a 7.
            if (diasHastaSabado == 0) diasHastaSabado = 7;

            DateTime proximoSabado = hoy.AddDays(diasHastaSabado);

            _logger.LogInformation($"Buscando evaluaciones exclusivamente para el próximo sábado: {proximoSabado:dd/MM/yyyy}");

            // Prioriza FechaEntregaReal sobre la fecha original
            var evaluaciones = await db.Evaluaciones
                .Where(e =>
                    (e.FechaEntregaReal.HasValue && e.FechaEntregaReal.Value.Date == proximoSabado) ||
                    (!e.FechaEntregaReal.HasValue && e.FechaEntrega.Date == proximoSabado))
                .ToListAsync();

            _logger.LogInformation($"Se encontraron {evaluaciones.Count} evaluaciones para publicar.");

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

            await AlertarSiCarteleraCrecioDemasiadoAsync(databaseId);

            _logger.LogInformation("✅ Sincronización de la cartelera completada.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Error sincronizando la cartelera: {ex.Message}");
        }
    }

    // Archiva en Notion (las manda a la papelera) las entradas de la Cartelera cuya fecha ya pasó.
    // Notion vacía la papelera solo pasados 30 días, pero esto evita que la tabla activa que cuenta
    // para el límite de bloques siga creciendo cada semana sin control.
    private async Task LimpiarEntradasPasadasAsync(string databaseId)
    {
        DateTime hoy = DateTime.UtcNow.Date;
        int archivadas = 0;
        string? cursor = null;

        do
        {
            var queryParams = new DatabasesQueryParameters
            {
                Filter = new DateFilter("Fecha de Entrega o Publicación", before: hoy),
                StartCursor = cursor
            };

            var response = await _notionClient.Databases.QueryAsync(databaseId, queryParams);

            foreach (var page in response.Results.OfType<Page>())
            {
                try
                {
                    await _notionClient.Pages.UpdateAsync(page.Id, new PagesUpdateParameters { InTrash = true });
                    archivadas++;
                    await Task.Delay(350);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ No se pudo archivar la entrada {page.Id} de la Cartelera: {ex.Message}");
                }
            }

            cursor = response.HasMore ? response.NextCursor : null;
        }
        while (cursor != null);

        if (archivadas > 0)
        {
            _logger.LogInformation($"🧹 Se archivaron {archivadas} entradas vencidas de la Cartelera.");
        }
    }

    // Si la Cartelera activa crece demasiado, avisamos por Telegram antes de que Notion
    // bloquee de sorpresa el workspace por falta de bloques.
    private async Task AlertarSiCarteleraCrecioDemasiadoAsync(string databaseId)
    {
        int total = 0;
        string? cursor = null;

        do
        {
            var queryParams = new DatabasesQueryParameters { StartCursor = cursor };
            var response = await _notionClient.Databases.QueryAsync(databaseId, queryParams);
            total += response.Results.Count;
            cursor = response.HasMore ? response.NextCursor : null;
        }
        while (cursor != null);

        if (total >= UmbralAlertaBloques)
        {
            _logDispatcher.EnqueueLog(
                $"⚠️ <b>Cartelera de Evaluaciones</b> tiene {total} entradas activas en Notion. " +
                "Si sigue creciendo así puede volver a agotar el plan gratuito de bloques. Revisa la papelera de Notion o considera vaciarla.",
                bypassLiveMode: true);
        }
    }
}
