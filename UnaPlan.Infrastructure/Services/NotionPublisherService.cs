using Microsoft.EntityFrameworkCore;
using Notion.Client;
using UnaPlan.Infrastructure.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;

namespace UnaPlan.Infrastructure.Services;

public class NotionPublisherService
{
    private readonly INotionClient _notionClient;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotionPublisherService> _logger;

    public NotionPublisherService(INotionClient notionClient, IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<NotionPublisherService> logger)
    {
        _notionClient = notionClient;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SincronizarCarteleraAsync()
    {
        // Usamos un Scope para proteger la memoria RAM del servidor
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Asegúrate de tener esta variable en Render y en tu appsettings.json
        var databaseId = _config["NotionSettings:CarteleraDatabaseId"];

        if (string.IsNullOrEmpty(databaseId))
        {
            _logger.LogError("El ID de la Cartelera de Notion no está configurado.");
            return;
        }

        try
        {
            // 1. Traer las evaluaciones de los próximos 30 días desde la base de datos
            var limiteFecha = DateTime.UtcNow.AddDays(30);
            var evaluaciones = await db.PlanesDeCurso
                .Include(p => p.Evaluaciones)
                .SelectMany(p => p.Evaluaciones)
                .Where(e => e.FechaEntrega >= DateTime.UtcNow && e.FechaEntrega <= limiteFecha)
                .ToListAsync();

            _logger.LogInformation($"Se encontraron {evaluaciones.Count} evaluaciones próximas para publicar.");

            // 2. Escribir cada evaluación en la nueva Cartelera de Notion
            foreach (var eval in evaluaciones)
            {
                var tipoEvaluacion = eval.Tipo ?? "Por definir";
                var materiaNombre = eval.NombreMateria ?? "Materia sin nombre";

                // Extraemos el código de forma segura (los primeros 3 caracteres si existen)
                var codigoMateria = materiaNombre.Length >= 3 ? materiaNombre.Substring(0, 3) : "N/A";

                // Propiedades de las columnas de la tabla principal
                var properties = new Dictionary<string, PropertyValue>
                {
                    { "Materia", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextText { Text = new Text { Content = materiaNombre } } } } },
                    { "Código", new RichTextPropertyValue { RichText = new List<RichTextBase> { new RichTextText { Text = new Text { Content = codigoMateria } } } } },
                    { "Tipo", new SelectPropertyValue { Select = new SelectOption { Name = tipoEvaluacion } } },
                    { "Fecha de Entrega", new DatePropertyValue { Date = new Date { Start = eval.FechaEntrega } } },
                    { "Semana", new NumberPropertyValue { Number = 1 } } // Ajusta esto si tienes la lógica de semanas
                };

                // Contenido interno de la página (El texto que pediste al final, usando negritas en vez de Heading para evitar errores)
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
                                    Annotations = new Annotations { IsBold = true } // Título en negrita seguro
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

                // Crear la fila en Notion
                await _notionClient.Pages.CreateAsync(new PagesCreateParameters
                {
                    Parent = new DatabaseParentInput { DatabaseId = databaseId },
                    Properties = properties,
                    Children = pageContent // Escribe dentro del cuerpo de la página
                });

                _logger.LogInformation($"Publicado en cartelera: {materiaNombre} - {tipoEvaluacion}");

                // Freno de 0.4 segundos (Límite de velocidad estricto de Notion)
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
