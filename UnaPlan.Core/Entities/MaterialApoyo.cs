namespace UnaPlan.Core.Entities;

public class MaterialApoyo
{
    public int Id { get; set; } // Autoincrementable

    // Llave Foránea para saber a qué Plan de Curso pertenece
    public string CodigoMateria { get; set; } = string.Empty;

    // Ej: "Libro de Cálculo de Stewart", "Guía de Ejercicios Prácticos"
    public string Titulo { get; set; } = string.Empty;

    public string UrlDrive { get; set; } = string.Empty;
}