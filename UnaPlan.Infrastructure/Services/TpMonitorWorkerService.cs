using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Services;

public class TpMonitorWorkerService : BackgroundService
{
    private readonly ILogger<TpMonitorWorkerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // Memoria RAM para evitar escaneos dobles en el mismo minuto
    private int _ultimoMinutoEjecucion = -1;

    public TpMonitorWorkerService(ILogger<TpMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🟢 Micro-Worker TP iniciado: TPs (Sábados, cada 15 min hasta las 8:00 AM).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var horaVenezuela = DateTime.UtcNow.AddHours(-4);

                // Lógica estricta: Solo hasta las 8:00 AM (ni un minuto más)
                bool esAntesOIgualA8AM = horaVenezuela.Hour < 8 || (horaVenezuela.Hour == 8 && horaVenezuela.Minute == 0);

                if (horaVenezuela.DayOfWeek == DayOfWeek.Saturday && esAntesOIgualA8AM)
                {
                    if (horaVenezuela.Minute % 15 == 0 && _ultimoMinutoEjecucion != horaVenezuela.Minute)
                    {
                        _ultimoMinutoEjecucion = horaVenezuela.Minute;
                        _logger.LogInformation($"[TP Monitor] Hora detectada ({horaVenezuela:HH:mm}). Iniciando escaneo de TPs...");

                        using var scope = _scopeFactory.CreateScope();
                        var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();

                        await scraper.EscanearTpPendientesAsync();

                        _logger.LogInformation("[TP Monitor] Escaneo finalizado. Liberando memoria...");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error en TP Monitor: {ex.Message}");
            }

            // Despertamos cada 30 segundos para garantizar no saltarnos el minuto exacto
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}