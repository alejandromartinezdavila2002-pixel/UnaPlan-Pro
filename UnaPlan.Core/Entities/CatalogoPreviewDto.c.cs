namespace UnaPlan.Core.Entities;

// DTO (Data Transfer Object): Un objeto ligero solo para transportar datos a la pantalla
public class CatalogoPreviewDto
{
    public string CodigoMateria { get; set; } = string.Empty;
    public string NombreArchivoPlan { get; set; } = string.Empty;
    public string UrlPlanCurso { get; set; } = string.Empty;

    // Una materia puede tener múltiples carreras que la ven
    public List<string> CarrerasQueLaVen { get; set; } = new();

    // Lista de materiales de apoyo encontrados
    public List<MaterialApoyoPreview> Materiales { get; set; } = new();
}

public class MaterialApoyoPreview
{
    public string NombreCarpeta { get; set; } = string.Empty;
    public string UrlDrive { get; set; } = string.Empty;
}