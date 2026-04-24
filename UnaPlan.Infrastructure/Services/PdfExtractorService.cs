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
        var nombreArchivo = Path.GetFileName(rutaArchivo);
        var evaluacionesEncontradas = new List<MateriaEvaluacion>();

        // Abrimos el PDF directamente en la memoria RAM
        using (var document = PdfDocument.Open(rutaArchivo))
        {
            foreach (var page in document.GetPages())
            {
                var palabras = page.GetWords().ToList();

                // ========================================================================
                // 1. AGRUPAMIENTO ESPACIAL INTELIGENTE (Tolerancia Dinámica)
                // ========================================================================
                var lineas = new List<List<Word>>();

                // Ordenar las palabras desde la parte superior de la página hacia abajo
                var palabrasOrdenadas = palabras.OrderByDescending(w => w.BoundingBox.Bottom).ToList();

                foreach (var palabra in palabrasOrdenadas)
                {
                    bool asignada = false;
                    foreach (var linea in lineas)
                    {
                        var palabraRef = linea.First();

                        // EL SECRETO: Tolerancia dinámica basada en la altura real de la fuente (40% de su tamaño)
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


                // ========================================================================
                // 2. PROCESAMIENTO DE LAS FILAS ENSAMBLADAS
                // ========================================================================
                foreach (var linea in filas)
                {
                    // FILTRO DE LLAVE PRIMARIA
                    // Verificamos si la línea comienza exactamente con 3 números (Ej: "115")
                    var matchCodigo = Regex.Match(linea, @"^(\d{3})\b");

                    if (matchCodigo.Success)
                    {
                        var codigo = matchCodigo.Groups[1].Value;
                        var matchesEval = EvalRegex.Matches(linea);

                        // Si encontramos fechas de evaluación en esta línea, procedemos
                        if (matchesEval.Count > 0)
                        {
                            // 1. EXTRACCIÓN MILIMÉTRICA DEL NOMBRE
                            // Calculamos el espacio exacto entre el código y el primer TP/TSP
                            int inicioNombre = matchCodigo.Index + matchCodigo.Length;
                            int finNombre = matchesEval[0].Index;

                            // Extraemos el texto del medio
                            string nombreMateriaCrudo = linea.Substring(inicioNombre, finNombre - inicioNombre);

                            // 2. LIMPIEZA AVANZADA (Sanitización)
                            // Quitamos espacios extra al inicio y final, y reducimos los espacios dobles a uno solo
                            string nombreLimpio = Regex.Replace(nombreMateriaCrudo.Trim(), @"\s+", " ");

                            // 3. FORMATO PROFESIONAL (Title Case)
                            // Convertimos "INgEniEría DE siStemas" a "Ingeniería De Sistemas"
                            var cultureInfo = new System.Globalization.CultureInfo("es-ES", false);
                            string nombreBase = cultureInfo.TextInfo.ToTitleCase(nombreLimpio.ToLower());

                            string nombreFormateado = Regex.Replace(nombreBase, @"\b(Ii{0,2}|Iv|Vi{0,2}|Ix|X)\b",
                            m => m.Value.ToUpper(), RegexOptions.IgnoreCase);

                            // 4. MAPEO Y CONSTRUCCIÓN DE OBJETOS
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