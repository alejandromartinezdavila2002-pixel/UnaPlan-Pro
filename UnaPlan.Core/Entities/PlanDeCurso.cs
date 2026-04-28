using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnaPlan.Core.Entities;

public class PlanDeCurso
{
    // Agregamos [Key] explícitamente para que EF Core no tenga dudas
    [Key]
    public string CodigoMateria { get; set; } = string.Empty;

    public string NombreMateria { get; set; } = string.Empty;

    // El link del PDF oficial del Plan de Curso
    public string UrlDocumento { get; set; } = string.Empty;

    // RELACIÓN (1 a Muchos): Un Plan de Curso puede tener VARIOS Materiales de Apoyo
    public List<MaterialApoyo> MaterialesDeApoyo { get; set; } = new();

    // Relación: Una materia tiene muchas evaluaciones
    // (Quitamos el [ForeignKey] de aquí, la responsabilidad la tendrá la clase hija)
    public List<MateriaEvaluacion> Evaluaciones { get; set; } = new();
}
