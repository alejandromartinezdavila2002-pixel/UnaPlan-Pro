using Microsoft.EntityFrameworkCore;
using UnaPlan.Infrastructure.Data;
using UnaPlan.Core.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnaPlan.Infrastructure.Services;

public class TspMonitorWorkerService : BackgroundService
{
    private readonly ILogger<TspMonitorWorkerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // Memoria caché interna (Protege la base de datos y la CPU)
    private int _ultimoDiaRevisado = -1;
    private bool _yaSeValidoHoy = false; // Bandera de estado para evitar el bug silencioso
    private bool _hayTspParaHoy = false;
    private List<string> _materiasTspEsperadas = new List<string>();
    private int _ultimoMinutoEscaneado = -1;

    public TspMonitorWorkerService(ILogger<TspMonitorWorkerService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🕵️‍♂️ Vigilante de TSP Activado (Modo Sabatino Inteligente).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var veTime = DateTime.UtcNow.AddHours(-4);

                // 1. Reseteo universal de variables al cambiar de día (sin importar la hora)
                if (veTime.Day != _ultimoDiaRevisado)
                {
                    _ultimoDiaRevisado = veTime.Day;
                    _yaSeValidoHoy = false;
                    _hayTspParaHoy = false;
                    _materiasTspEsperadas.Clear();
                    _ultimoMinutoEscaneado = -1;
                }

                // 2. Ahorro extremo de CPU en Render para días de semana
                if (veTime.DayOfWeek != DayOfWeek.Saturday)
                {
                    // Si no es sábado, dormimos el Worker por 1 hora entera
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    continue; // Salta el resto del código y vuelve a empezar el bucle
                }

                // 3. FASE 1: PRE-VALIDACIÓN (A partir de las 5:00 AM usando la bandera booleana)
                if (veTime.Hour >= 5 && !_yaSeValidoHoy)
                {
                    await ValidarTspProgramadosParaHoyAsync(veTime, stoppingToken);
                    _yaSeValidoHoy = true;
                }

                // 4. FASE 2: BÚSQUEDA Y EXTRACCIÓN DINÁMICA
                if (_hayTspParaHoy && veTime.Hour >= 6)
                {
                    // Límite de Cordura (Hard Stop): Las 14:00 (2:00 PM)
                    if (veTime.Hour >= 14)
                    {
                        _logger.LogWarning($"[TSP] ⚠️ Límite de las 2:00 PM alcanzado. La universidad no publicó los TSP de: {string.Join(", ", _materiasTspEsperadas)}. Abortando búsqueda.");
                        _hayTspParaHoy = false;
                        _materiasTspEsperadas.Clear();
                    }
                    else
                    {
                        // Escanear en minutos desfasados (05, 20, 35, 50)
                        if ((veTime.Minute == 5 || veTime.Minute == 20 || veTime.Minute == 35 || veTime.Minute == 50)
                            && veTime.Minute != _ultimoMinutoEscaneado)
                        {
                            _ultimoMinutoEscaneado = veTime.Minute;
                            _logger.LogInformation($"[TSP] Arrancando escáner Drive. Faltan {_materiasTspEsperadas.Count} TSPs por publicar...");

                            using var scope = _scopeFactory.CreateScope();

                            // Instanciamos el scraper real y le pasamos la fecha de hoy
                            var scraper = scope.ServiceProvider.GetRequiredService<CatalogoScraperService>();
                            await scraper.EscanearTspAsync(veTime.Date);

                            // Evaluamos qué se encontró para hacer el Cierre Prematuro si aplica
                            await ActualizarMateriasFaltantesAsync(stoppingToken);
                        }
                    }
                }

                // Espera normal de 30 segundos durante los sábados
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error crítico en el vigilante de TSP.");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
    }

    private async Task ValidarTspProgramadosParaHoyAsync(DateTime veTime, CancellationToken token)
    {
        _logger.LogInformation("[TSP] 5:00 AM alcanzadas. Consultando Calendario de Evaluaciones para hoy...");

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var materiasProgramadas = await dbContext.Evaluaciones
            .Where(e => e.Tipo.StartsWith("TSP") && e.FechaEntrega.Date == veTime.Date && e.CodigoMateria != null)
            .Select(e => e.CodigoMateria!)
            .Distinct()
            .ToListAsync(token);

        if (materiasProgramadas.Any())
        {
            _hayTspParaHoy = true;
            _materiasTspEsperadas = materiasProgramadas;
            _logger.LogInformation($"[TSP] ✅ ALERTA: El calendario indica TSP para {materiasProgramadas.Count} materias hoy. El escáner Drive arrancará a las 6:00 AM.");
        }
        else
        {
            _hayTspParaHoy = false;
            _logger.LogInformation("[TSP] 💤 El calendario está vacío para hoy. El escáner Drive no se activará en todo el fin de semana.");
        }

        // Ya no es estrictamente necesario, pero se mantiene por doble seguridad
        _ultimoDiaRevisado = veTime.Day;
    }

    private async Task ActualizarMateriasFaltantesAsync(CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var materiasEncontradas = await dbContext.TrabajosPublicados
            .Where(t => t.Evaluacion != null
                        && t.Evaluacion.Tipo.StartsWith("TSP")
                        && t.Evaluacion.CodigoMateria != null
                        && _materiasTspEsperadas.Contains(t.Evaluacion.CodigoMateria))
            .Select(t => t.Evaluacion!.CodigoMateria!)
            .Distinct()
            .ToListAsync(token);

        foreach (var materia in materiasEncontradas)
        {
            _materiasTspEsperadas.Remove(materia);
            _logger.LogInformation($"[TSP] ✅ TSP de la materia {materia} encontrado y guardado. Removido de la cola de espera.");
        }

        if (!_materiasTspEsperadas.Any())
        {
            _logger.LogInformation("[TSP] 🎉 ¡Éxito total! Todos los TSPs programados para hoy fueron publicados. El vigilante de TSP se apaga hasta el próximo sábado.");
            _hayTspParaHoy = false;
        }
    }
}