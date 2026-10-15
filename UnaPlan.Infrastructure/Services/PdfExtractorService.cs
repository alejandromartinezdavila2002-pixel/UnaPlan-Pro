using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UnaPlan.Core.Entities;

namespace UnaPlan.Infrastructure.Services;

public class PdfExtractorService
{
    // Regex compilado para alto rendimiento. 
    // Atrapa patrones como "TP 12 21/03/2026" o "TSP1 11 14/03/2026"
    private static readonly Regex EvalRegex = new Regex(
        @"(?<tipo>TP\d?|TSP\d?)\s+(?<semana>\d+)\s+(?<fecha>\d{2}/\d{2}/\d{4})",
        RegexOptions.Compiled);

    public CalendarioProcesado ProcesarCalendarioPdf(string rutaArchivo)
    {
        using var stream = File.OpenRead(rutaArchivo);
        return ProcesarCalendarioPdf(stream, Path.GetFileName(rutaArchivo));
    }

    // Sobrecarga para PDFs que llegan en memoria (ej. descargados desde Telegram)
    public CalendarioProcesado ProcesarCalendarioPdf(Stream pdf, string nombreArchivo)
    {
        var evaluacionesEncontradas = new List<MateriaEvaluacion>();

        // Abrimos el PDF directamente en la memoria RAM
        using (var document = PdfDocument.Open(pdf))
        {
            foreach (var page in document.GetPages())
            {
                var palabras = page.GetWords().ToList();

                // Agrupamos las palabras en líneas por posición, porque el PDF no nos da líneas de texto
                var lineas = new List<List<Word>>();

                // De arriba hacia abajo
                var palabrasOrdenadas = palabras.OrderByDescending(w => w.BoundingBox.Bottom).ToList();

                foreach (var palabra in palabrasOrdenadas)
                {
                    bool asignada = false;
                    foreach (var linea in lineas)
                    {
                        var palabraRef = linea.First();

                        // La tolerancia es relativa al tamaño de la fuente (40%) para que funcione con cualquier tamaño de letra
                        double tolerancia = palabra.BoundingBox.Height * 0.4;

                        // Si la diferencia en el eje Y es menor a nuestra tolerancia dinámica, es la misma línea
                        if (Math.Abs(palabraRef.BoundingBox.Bottom - palabra.BoundingBox.Bottom) <= tolerancia)
                        {
                            linea.Add(palabra);
                            asignada = true;
                            break;
                        }
                    }

                    // Si no encajó en ninguna línea existente, creamos una nueva
                    if (!asignada)
                    {
                        lineas.Add(new List<Word> { palabra });
                    }
                }

                // Construir el texto final ordenando cada línea de izquierda a derecha
                var filas = lineas
                    .Select(linea => string.Join(" ", linea.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                    .ToList();


                foreach (var linea in filas)
                {
                    // Una línea de materia siempre arranca con el código de 3 dígitos (ej. "115")
                    var matchCodigo = Regex.Match(linea, @"^(\d{3})\b");

                    if (matchCodigo.Success)
                    {
                        var codigo = matchCodigo.Groups[1].Value;
                        var matchesEval = EvalRegex.Matches(linea);

                        // Si encontramos fechas de evaluación en esta línea, procedemos
                        if (matchesEval.Count > 0)
                        {
                            // El nombre de la materia es lo que queda entre el código y el primer TP/TSP
                            int inicioNombre = matchCodigo.Index + matchCodigo.Length;
                            int finNombre = matchesEval[0].Index;
                            string nombreMateriaCrudo = linea.Substring(inicioNombre, finNombre - inicioNombre);

                            // Espacios extra al inicio/final fuera, y los dobles espacios a uno solo
                            string nombreLimpio = Regex.Replace(nombreMateriaCrudo.Trim(), @"\s+", " ");

                            // De "INgEniEría DE siStemas" a "Ingeniería De Sistemas"
                            var cultureInfo = new System.Globalization.CultureInfo("es-ES", false);
                            string nombreBase = cultureInfo.TextInfo.ToTitleCase(nombreLimpio.ToLower());

                            // TitleCase deja "Ii"/"Iii"/"Iv" en minúscula a medias; los pasamos a mayúscula (números romanos)
                            string nombreFormateado = Regex.Replace(nombreBase, @"\b(Ii{0,2}|Iv|Vi{0,2}|Ix|X)\b",
                            m => m.Value.ToUpper(), RegexOptions.IgnoreCase);

                            foreach (Match m in matchesEval)
                            {
                                // Parseo de fecha en formato Latino/Europeo
                                var fechaLocal = DateTime.ParseExact(m.Groups["fecha"].Value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                                var fechaUtc = DateTime.SpecifyKind(fechaLocal, DateTimeKind.Utc);

                                evaluacionesEncontradas.Add(new MateriaEvaluacion
                                {
                                    CodigoMateria = codigo,
                                    // Si por alguna razón el espacio estaba vacío, dejamos un fallback de seguridad
                                    NombreMateria = string.IsNullOrWhiteSpace(nombreFormateado) ? $"Materia {codigo} (Sin Nombre)" : nombreFormateado,
                                    Tipo = m.Groups["tipo"].Value,
                                    Semana = int.Parse(m.Groups["semana"].Value),
                                    FechaEntrega = fechaUtc
                                });
                            }
                        }
                    }
                }
            }
        }

        return new CalendarioProcesado
        {
            NombreArchivo = nombreArchivo,
            FechaProcesamiento = DateTime.UtcNow,
            Evaluaciones = evaluacionesEncontradas,
            DatosJsonRaw = "{}" // Aquí podríamos inyectar el JSON completo si se necesita auditoría
        };
    }
}
