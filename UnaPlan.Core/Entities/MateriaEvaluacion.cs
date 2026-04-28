using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnaPlan.Core.Entities;

public class MateriaEvaluacion
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Tipo { get; set; } = string.Empty; // "TP" o "TSP"

    public int? Semana { get; set; }

    public DateTime FechaEntrega { get; set; }

    // NUEVO: La fecha que el Worker extraerá del PDF (incluyendo hora límite 23:59)
    public DateTime? FechaEntregaReal { get; set; }

    [Required]
    public string NombreMateria { get; set; } = string.Empty;

    public string? CodigoMateria { get; set; }

    // Relación original con el Plan de Curso
    public int PlanDeCursoId { get; set; }
    [ForeignKey("PlanDeCursoId")]
    public PlanDeCurso? PlanDeCurso { get; set; }

    // NUEVO: Relación 1 a 1 con el trabajo publicado en Drive
    public TrabajosPublicados? TrabajoPublicado { get; set; }
}
