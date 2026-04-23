using System.ComponentModel.DataAnnotations.Schema;

namespace UnaPlan.Core.Entities;

public class PlanDeCurso
{
    // El código 370 será nuestra Llave Primaria
    public string CodigoMateria { get; set; } = string.Empty;
    public string NombreMateria { get; set; } = string.Empty;

    // El link del PDF oficial del Plan de Curso (El que vimos en tu captura de Drive)
    public string UrlDocumento { get; set; } = string.Empty;

    // RELACIÓN (1 a Muchos): Un Plan de Curso puede tener VARIOS Materiales de Apoyo
    public List<MaterialApoyo> MaterialesDeApoyo { get; set; } = new();

    // Relación: Una materia tiene muchas evaluaciones
    [ForeignKey("CodigoMateria")]
    public List<MateriaEvaluacion> Evaluaciones { get; set; } = new();

}