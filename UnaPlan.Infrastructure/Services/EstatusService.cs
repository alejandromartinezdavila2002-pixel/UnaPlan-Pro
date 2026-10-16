using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

/// <summary>Conteo de un tipo de evaluación (TP o TSP) del periodo cargado.</summary>
public class EstatusEvaluaciones
{
    public int Total { get; set; }
    public int Encontrados { get; set; }
    public int Faltan => Total - Encontrados;

    // Desglose de lo que falta según su fecha de entrega
    public int FaltanFechasPasadas { get; set; }
    public int FaltanHoy { get; set; }
    public int FaltanProximos { get; set; }

    // Solo los de hoy (para el sábado de TSP)
    public int TotalHoy { get; set; }
    public int EncontradosHoy { get; set; }

    // Próxima fecha (después de hoy) con evaluaciones pendientes
    public DateTime? ProximaFecha { get; set; }
    public int ProximaCantidad { get; set; }
}

public class EstatusSistema
{
    public DateTime HoraVenezuela { get; set; }

    public int Estudiantes { get; set; }
    public int EstudiantesConMaterias { get; set; }
    public int EstudiantesSinMaterias => Estudiantes - EstudiantesConMaterias;
    public int InscritosHoy { get; set; }

    public EstatusEvaluaciones Tsp { get; set; } = new();
    public EstatusEvaluaciones Tp { get; set; } = new();

    public int CorreosHoy { get; set; }
    public int LimiteCorreosDiario { get; set; }
    public int CorreosRestantes => Math.Max(0, LimiteCorreosDiario - CorreosHoy);

    /// <summary>Si el servidor arrancó hoy, el contador de correos solo cuenta desde esa hora (VE).</summary>
    public DateTime? ContadorDesdeVenezuela { get; set; }

    public EstadoConvocatoria Convocatoria { get; set; } = new();
}

// Foto del sistema para el botón "📊 Estatus" de Telegram. Todo sale de la BD
// (sobrevive a reinicios), salvo el contador de correos del día, que vive en memoria.
public class EstatusService
{
    private static readonly TimeSpan OffsetVenezuela = TimeSpan.FromHours(-4);
    private static readonly DateTime ArranqueUtc = ObtenerArranqueUtc();

    private readonly AppDbContext _db;
    private readonly PeriodoAcademicoService _periodo;

    public EstatusService(AppDbContext db, PeriodoAcademicoService periodo)
    {
        _db = db;
        _periodo = periodo;
    }

    public async Task<EstatusSistema> ObtenerAsync(CancellationToken ct = default)
    {
        var ahoraVe = DateTime.UtcNow.Add(OffsetVenezuela);
        var hoyVe = ahoraVe.Date;

        // Medianoche de Venezuela expresada en UTC (las columnas son timestamptz: Kind debe ser Utc)
        var inicioHoyUtc = DateTime.SpecifyKind(hoyVe - OffsetVenezuela, DateTimeKind.Utc);

        var estatus = new EstatusSistema { HoraVenezuela = ahoraVe };

        // Estudiantes
        estatus.Estudiantes = await _db.EstudiantesSuscritos.CountAsync(ct);
        estatus.EstudiantesConMaterias = await _db.EstudiantesSuscritos.CountAsync(e => e.MateriasInscritas.Count > 0, ct);
        // FechaSuscripcion se renueva cada vez que alguien registra sus materias (nuevo o existente)
        estatus.InscritosHoy = await _db.EstudiantesSuscritos.CountAsync(e => e.FechaSuscripcion >= inicioHoyUtc, ct);

        // Evaluaciones: son unos cientos de filas por periodo, se agrupan en memoria
        var evaluaciones = await _db.Evaluaciones
            .AsNoTracking()
            .Select(e => new { e.Tipo, e.FechaEntrega, Encontrado = e.TrabajoPublicado != null })
            .ToListAsync(ct);

        // FechaEntrega se guarda como la fecha a las 00:00 UTC, así que .Date es el día del calendario
        var filas = evaluaciones.Select(e => (e.Tipo, Fecha: e.FechaEntrega.Date, e.Encontrado)).ToList();

        estatus.Tsp = Contar(filas.Where(e => e.Tipo.StartsWith("TSP", StringComparison.OrdinalIgnoreCase)), hoyVe);
        estatus.Tp = Contar(filas.Where(e => e.Tipo.StartsWith("TP", StringComparison.OrdinalIgnoreCase)), hoyVe);

        // Correos
        var (enviados, limite) = EmailService.ObtenerContadorDeHoy();
        estatus.CorreosHoy = enviados;
        estatus.LimiteCorreosDiario = limite;
        if (ArranqueUtc > inicioHoyUtc)
            estatus.ContadorDesdeVenezuela = ArranqueUtc.Add(OffsetVenezuela);

        estatus.Convocatoria = await _periodo.ObtenerEstadoConvocatoriaAsync();

        return estatus;
    }

    private static EstatusEvaluaciones Contar(IEnumerable<(string Tipo, DateTime Fecha, bool Encontrado)> evaluaciones, DateTime hoy)
    {
        var lista = evaluaciones.ToList();
        var pendientes = lista.Where(e => !e.Encontrado).ToList();

        var r = new EstatusEvaluaciones
        {
            Total = lista.Count,
            Encontrados = lista.Count(e => e.Encontrado),
            FaltanFechasPasadas = pendientes.Count(e => e.Fecha < hoy),
            FaltanHoy = pendientes.Count(e => e.Fecha == hoy),
            FaltanProximos = pendientes.Count(e => e.Fecha > hoy),
            TotalHoy = lista.Count(e => e.Fecha == hoy),
            EncontradosHoy = lista.Count(e => e.Fecha == hoy && e.Encontrado)
        };

        var proxima = pendientes.Where(e => e.Fecha > hoy).Select(e => e.Fecha).DefaultIfEmpty().Min();
        if (proxima != default)
        {
            r.ProximaFecha = proxima;
            r.ProximaCantidad = pendientes.Count(e => e.Fecha == proxima);
        }

        return r;
    }

    private static DateTime ObtenerArranqueUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }
}
