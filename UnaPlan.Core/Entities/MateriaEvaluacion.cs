namespace UnaPlan.Core.Entities;

public class MateriaEvaluacion
{
    public int Id { get; set; }
    public string CodigoMateria { get; set; } = string.Empty; // Nuestra llave primaria logica
    public string NombreMateria { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty; // TP o TSP
    public int Semana { get; set; }
    public DateTime FechaEntrega { get; set; }
}

