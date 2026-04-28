using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public class DriveMonitorWorkerService : BackgroundService
{
    private readonly ILogger<DriveMonitorWorkerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // Reloj interno para los Trabajos Prácticos (Empieza en MinValue para que busque inmediatamente la primera vez)
    private DateTime _ultimoChequeoTp = DateTime.MinValue;

    public DriveMonitorWorkerService(ILogger<DriveMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("👁️ Vigilante de Drive activado. Monitoreando TSPs (Sábados) y TPs (Cada 10 días).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var horaVenezuela = DateTime.UtcNow.AddHours(-4);
                var hoy = horaVenezuela.Date;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();

                // =======================================================
                // 1. TAREA ESTRATÉGICA: Buscar TPs (Cada 10 días)
                // =======================================================
                if ((horaVenezuela - _ultimoChequeoTp).TotalDays >= 10)
                {
                    _logger.LogInformation("🔍 Iniciando escaneo de rutina para Trabajos Prácticos (TP) pendientes...");
                    await scraper.EscanearTpPendientesAsync();
                    _ultimoChequeoTp = horaVenezuela; // Reiniciamos el reloj de 10 días
                    _logger.LogInformation("✅ Escaneo de TPs finalizado.");
                }

                // =======================================================
                // 2. TAREA TÁCTICA: Buscar TSPs (Sábados con entregas)
                // =======================================================
                if (hoy.DayOfWeek == DayOfWeek.Saturday)
                {
                    var hayTspHoy = await db.Evaluaciones
                        .AnyAsync(e => e.Tipo.Contains("TSP") && e.FechaEntrega.Date == hoy, stoppingToken);

                    if (hayTspHoy)
                    {
                        _logger.LogInformation($"🚀 Sábado de entregas TSP detectado ({hoy:dd/MM/yyyy}). Escaneando Drive...");
                        await scraper.EscanearTspAsync(hoy);

                        _logger.LogInformation("Escaneo TSP finalizado. Próximo chequeo en 15 minutos.");
                        await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                        continue; // Evita el delay largo de abajo
                    }
                }

                // Si no hay emergencia de TSP, duerme 4 horas
                await Task.Delay(TimeSpan.FromHours(4), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error en el vigilante de Drive: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }
}
