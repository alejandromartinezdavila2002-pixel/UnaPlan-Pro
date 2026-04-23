namespace UnaPlan.Core.Entities;

public class CalendarioProcesado
{
    // nota
    public int Id { get; set; }
    public string NombreArchivo { get; set; } = string.Empty; // Ej: "Calendario_2026_1.pdf"
    public DateTime FechaProcesamiento { get; set; }

    // Aqui guardamos el JSON completo de lo extraido para verificacion rapida
    public string DatosJsonRaw { get; set; } = string.Empty;

    // Relacion con las materias individuales
    public List<MateriaEvaluacion> Evaluaciones { get; set; } = new();
}

