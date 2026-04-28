using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnaPlan.Core.Entities;

public class TrabajosPublicados
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UrlDrive { get; set; } = string.Empty;

    [Required]
    public string Tipo { get; set; } = string.Empty; // "TP" o "TSP"

    public DateTime FechaPublicacion { get; set; } = DateTime.UtcNow;

    // Relación 1 a 1: Un Trabajo Publicado pertenece a una Evaluación específica
    [Required]
    public int MateriaEvaluacionId { get; set; }

    [ForeignKey("MateriaEvaluacionId")]
    public MateriaEvaluacion? Evaluacion { get; set; }
}
