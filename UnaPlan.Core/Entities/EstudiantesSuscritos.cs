using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace UnaPlan.Core.Entities;

public class EstudiantesSuscritos
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Nombre { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Correo { get; set; } = string.Empty;

    // Postgres lo guarda como text[] gracias al mapeo de EF Core
    public List<string> MateriasInscritas { get; set; } = new();

    public DateTime FechaSuscripcion { get; set; } = DateTime.UtcNow;

    // Fecha del ultimátum (15 días). Null si el estudiante está activo y no tiene advertencias pendientes.
    public DateTime? FechaAdvertencia { get; set; }

    // CRM: cuándo recibió la convocatoria del periodo actual. Null = pendiente.
    // El reinicio de semestre la vuelve a null; permite reanudar la convocatoria
    // al día siguiente sin reenviársela a quien ya la recibió.
    public DateTime? FechaConvocatoria { get; set; }
}
