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

    // Magia de PostgreSQL + EF Core 9: Esto se guardará como un text[] en la base de datos
    public List<string> MateriasInscritas { get; set; } = new();

    public DateTime FechaSuscripcion { get; set; } = DateTime.UtcNow;

    // NUEVA COLUMNA CRM: Registra la fecha del ultimátum (15 días). 
    // Es Nullable (DateTime?) porque los estudiantes activos no tendrán advertencias pendientes.
    public DateTime? FechaAdvertencia { get; set; }

    // CRM: cuándo recibió la convocatoria del periodo actual. Null = pendiente.
    // El reinicio de semestre la vuelve a null; permite reanudar la convocatoria
    // al día siguiente sin reenviársela a quien ya la recibió.
    public DateTime? FechaConvocatoria { get; set; }
}
