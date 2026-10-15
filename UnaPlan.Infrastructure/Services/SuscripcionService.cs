using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnaPlan.Core.Entities;
using UnaPlan.Infrastructure.Data;

namespace UnaPlan.Infrastructure.Services;

public record TrabajoParaCorreo(string CodigoMateria, string Tipo, DateTime FechaEntrega, string UrlDrive);

public class ResultadoSuscripcion
{
    public bool EsNuevo { get; set; }
    public int TrabajosEnviados { get; set; }
    /// <summary>Si el correo de trabajos ya publicados falló. La suscripción igual queda guardada.</summary>
    public string? ErrorEnvioTrabajos { get; set; }
}

// Punto único de suscripción: lo usan el endpoint /solicitar-plan y el NotionWorkerService
public class SuscripcionService
{
    private static readonly char[] Separadores = { ',', '.', ' ', '-', ';' };

    private readonly AppDbContext _db;
    private readonly EmailService _emailService;
    private readonly ILogger<SuscripcionService> _logger;

    public SuscripcionService(AppDbContext db, EmailService emailService, ILogger<SuscripcionService> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>Parser único de códigos de materia (acepta , . espacio - ;).</summary>
    public static List<string> ParsearMaterias(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        return raw.Split(Separadores, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Distinct()
                  .ToList();
    }

    /// <summary>
    /// Guarda o actualiza al estudiante y le envía en un solo correo los trabajos
    /// (TP/TSP) ya publicados de sus materias que todavía no han vencido.
    /// </summary>
    public async Task<ResultadoSuscripcion> SuscribirAsync(string nombre, string correo, List<string> materias)
    {
        var resultado = new ResultadoSuscripcion();
        correo = correo.Trim();
        var correoNormalizado = correo.ToLower();

        var estudiante = await _db.EstudiantesSuscritos
            .FirstOrDefaultAsync(e => e.Correo.ToLower() == correoNormalizado);

        if (estudiante != null)
        {
            estudiante.Nombre = nombre;
            estudiante.MateriasInscritas = materias;
            estudiante.FechaSuscripcion = DateTime.UtcNow;
            estudiante.FechaAdvertencia = null; // Se reactivó: el ultimátum anterior ya no aplica
        }
        else
        {
            estudiante = new EstudiantesSuscritos
            {
                Nombre = nombre,
                Correo = correo,
                MateriasInscritas = materias,
                FechaSuscripcion = DateTime.UtcNow
            };
            await _db.EstudiantesSuscritos.AddAsync(estudiante);
            resultado.EsNuevo = true;
        }

        await _db.SaveChangesAsync();

        // Ponerse al día: los trabajos que salieron ANTES de suscribirse no generan alerta
        // (las alertas solo se disparan al registrar el trabajo), así que se los mandamos aquí.
        try
        {
            var trabajos = await ObtenerTrabajosVigentesAsync(materias);
            if (trabajos.Any())
            {
                await _emailService.EnviarTrabajosYaPublicadosAsync(correo, nombre, trabajos);
                resultado.TrabajosEnviados = trabajos.Count;
            }
        }
        catch (Exception ex)
        {
            resultado.ErrorEnvioTrabajos = ex.Message;
            _logger.LogError($"No se pudo enviar el resumen de trabajos publicados a {correo}: {ex.Message}");
        }

        return resultado;
    }

    private async Task<List<TrabajoParaCorreo>> ObtenerTrabajosVigentesAsync(List<string> materias)
    {
        if (materias.Count == 0) return new List<TrabajoParaCorreo>();

        // Margen de 1 día para no dejar fuera lo que vence hoy (hora Venezuela)
        var limite = DateTime.UtcNow.AddDays(-1);

        var trabajos = await _db.TrabajosPublicados
            .Where(t => t.Evaluacion != null
                        && t.Evaluacion.CodigoMateria != null
                        && materias.Contains(t.Evaluacion.CodigoMateria)
                        && (t.Evaluacion.FechaEntregaReal ?? t.Evaluacion.FechaEntrega) >= limite)
            .Select(t => new TrabajoParaCorreo(
                t.Evaluacion!.CodigoMateria!,
                t.Evaluacion.Tipo,
                t.Evaluacion.FechaEntregaReal ?? t.Evaluacion.FechaEntrega,
                t.UrlDrive))
            .ToListAsync();

        // Una misma materia puede tener varias filas en Evaluaciones apuntando al mismo archivo
        return trabajos
            .DistinctBy(t => (t.CodigoMateria, t.Tipo, t.UrlDrive))
            .OrderBy(t => t.FechaEntrega)
            .ThenBy(t => t.CodigoMateria)
            .ToList();
    }
}
