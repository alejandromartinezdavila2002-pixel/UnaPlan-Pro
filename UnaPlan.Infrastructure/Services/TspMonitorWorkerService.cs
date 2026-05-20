using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Services;

public class TspMonitorWorkerService : BackgroundService
{
    private readonly ILogger<TspMonitorWorkerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // Memoria RAM para evitar ejecuciones dobles en el mismo minuto
    private int _ultimoMinutoEjecucion = -1;

    public TspMonitorWorkerService(ILogger<TspMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🟢 Micro-Worker TSP iniciado: TSPs (Desfasado: minutos 5, 20, 35, 50 entre 6 AM y 8 AM).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var horaVenezuela = DateTime.UtcNow.AddHours(-4);

                // Ventana de patrullaje: Desde las 6:00 AM hasta las 8:05 AM (para abarcar el último turno del desfase)
                bool esHorarioPlanificado = horaVenezuela.Hour >= 6 && (horaVenezuela.Hour < 8 || (horaVenezuela.Hour == 8 && horaVenezuela.Minute <= 5));

                if (esHorarioPlanificado)
                {
                    // Lógica de Desfase: Dispara 5 minutos DESPUÉS del TP (5, 20, 35, 50)
                    if (horaVenezuela.Minute % 15 == 5 && _ultimoMinutoEjecucion != horaVenezuela.Minute)
                    {
                        _ultimoMinutoEjecucion = horaVenezuela.Minute;
                        _logger.LogInformation($"[TSP Monitor] Turno activo ({horaVenezuela:HH:mm}). Comprobando calendario para hoy: {horaVenezuela:dd/MM/yyyy}");

                        using var scope = _scopeFactory.CreateScope();
                        var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();

                        // El scraper se encargará internamente de verificar si hoy hay un TSP antes de ir a Drive
                        await scraper.EscanearTspAsync(horaVenezuela.Date);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error en TSP Monitor: {ex.Message}");
            }

            // Despertamos cada 30 segundos para máxima precisión
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}