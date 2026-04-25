using ClosedXML.Excel;
using UnaPlan.Core.Entities;

namespace UnaPlan.Infrastructure.Services;

public class ExcelGeneratorService
{
    public class MateriaPlanillaDto
    {
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string TipoEvaluacion { get; set; } = "";
        public string FechaEntrega { get; set; } = "";
        public string UrlPlan { get; set; } = "";
        public List<string> UrlsMateriales { get; set; } = new();
    }

    public byte[] GenerarPlanDeEvaluacionExcel(List<MateriaPlanillaDto> materias)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Mi Plan UNA");

        // --- ESTILO GLOBAL: Centrado Vertical ---
        worksheet.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        // --- 1. ENCABEZADO SUPERIOR (Cubre de A hasta H) ---
        var filaTitulo = worksheet.Row(1);
        filaTitulo.Height = 40; 

        worksheet.Cell("A1").Value = "PLAN DE EVALUACIÓN PERSONALIZADO - UNA";
        // Expandimos el título hasta la columna H (8) para que abrace ambas tablas
        var titulo = worksheet.Range("A1:H1");
        titulo.Merge().Style
            .Font.SetBold()
            .Font.SetFontSize(18)
            .Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1e293b"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // --- 2. CABECERAS (FILA 3) ---
        worksheet.Row(3).Height = 25; 
        
        // Cabeceras Tabla Principal (Izquierda: Cols 1 a 4)
        string[] headers = { "CÓDIGO", "MATERIA", "EVALUACIÓN", "FECHA DE ENTREGA" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(3, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.FromHtml("#f1f5f9"))
                .Font.SetFontColor(XLColor.FromHtml("#475569"))
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1");
        }

        // Cabecera Tabla Recursos (Derecha: Cols 6 a 8)
        worksheet.Cell(3, 6).Value = "RECURSOS Y ENLACES OFICIALES";
        worksheet.Range(3, 6, 3, 8).Merge().Style
            .Font.SetBold()
            .Fill.SetBackgroundColor(XLColor.FromHtml("#eff6ff"))
            .Font.SetFontColor(XLColor.FromHtml("#1d4ed8"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        worksheet.Range(3, 6, 3, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        worksheet.Range(3, 6, 3, 8).Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1");


        // --- 3. CONSTRUIR TABLA PRINCIPAL (IZQUIERDA) ---
        int filaActual = 4;
        var gruposPorMateria = materias.GroupBy(m => m.Codigo);

        foreach (var grupo in gruposPorMateria)
        {
            int filaInicioMateria = filaActual;

            foreach (var eval in grupo)
            {
                worksheet.Row(filaActual).Height = 22; 
                worksheet.Cell(filaActual, 3).Value = eval.TipoEvaluacion;
                worksheet.Cell(filaActual, 4).Value = eval.FechaEntrega;
                worksheet.Cell(filaActual, 4).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed)
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                // Bordes internos suaves
                worksheet.Range(filaActual, 3, filaActual, 4).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                worksheet.Range(filaActual, 3, filaActual, 4).Style.Border.OutsideBorderColor = XLColor.FromHtml("#e2e8f0");

                filaActual++;
            }

            var primerItem = grupo.First();

            // Celda Código
            var rangoCodigo = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 1);
            rangoCodigo.Merge().Value = primerItem.Codigo;
            rangoCodigo.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Celda Nombre
            var rangoNombre = worksheet.Range(filaInicioMateria, 2, filaActual - 1, 2);
            string nombreLimpio = primerItem.Nombre.Replace(".pdf", "").Replace(".PDF", "").Trim();
            rangoNombre.Merge().Value = nombreLimpio;
            rangoNombre.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left);
            rangoNombre.Style.Alignment.SetIndent(1);

            // Borde exterior de la materia completa
            var rangoMateriaCompleta = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 4);
            rangoMateriaCompleta.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            rangoMateriaCompleta.Style.Border.OutsideBorderColor = XLColor.FromHtml("#94a3b8");

            filaActual++; // Separación entre bloques de materias
            worksheet.Row(filaActual - 1).Height = 8;
        }


        // --- 4. CONSTRUIR TABLA RECURSOS (DERECHA) ---
        int filaRecursos = 4;
        
        foreach (var mat in materias.DistinctBy(m => m.Codigo))
        {
            worksheet.Row(filaRecursos).Height = 22;
            
            // Columna 6 (F): Código
            worksheet.Cell(filaRecursos, 6).Value = $"Materia {mat.Codigo}";
            worksheet.Cell(filaRecursos, 6).Style.Font.SetBold().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Columna 7 (G): Link Plan
            var linkPlan = worksheet.Cell(filaRecursos, 7);
            if (!string.IsNullOrEmpty(mat.UrlPlan))
            {
                linkPlan.Value = "📄 Plan de Curso";
                string urlSeguraPlan = Uri.EscapeDataString(mat.UrlPlan);
                string urlMagicaPlan = $"https://unaplanapi.onrender.com/api/go?target={urlSeguraPlan}";
                linkPlan.SetHyperlink(new XLHyperlink(urlMagicaPlan));
                linkPlan.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }
            else
            {
                linkPlan.Value = "No disponible";
                linkPlan.Style.Font.SetFontColor(XLColor.Gray);
            }

            // Columna 8 (H): Link Materiales
            var linkMat = worksheet.Cell(filaRecursos, 8);
            if (mat.UrlsMateriales.Any() && !string.IsNullOrEmpty(mat.UrlsMateriales.First()))
            {
                linkMat.Value = "📁 Carpeta de Apoyo";
                string urlSeguraMat = Uri.EscapeDataString(mat.UrlsMateriales.First());
                string urlMagicaMat = $"https://unaplanapi.onrender.com/api/go?target={urlSeguraMat}";
                linkMat.SetHyperlink(new XLHyperlink(urlMagicaMat));
                linkMat.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }
            else
            {
                linkMat.Value = "-";
                linkMat.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }

            filaRecursos++; // Aquí no dejamos saltos de fila para que sea una lista compacta
        }


        // --- 5. IGUALAR ALTURAS Y ESTÉTICA DE LA BARRA LATERAL ---
        // Calculamos cuál de las dos tablas llegó más abajo
        int ultimaFilaGlobal = Math.Max(filaActual - 1, filaRecursos - 1);

        // Aplicamos el borde exterior al panel de recursos para que baje hasta igualar a la tabla izquierda
        var panelRecursos = worksheet.Range(4, 6, ultimaFilaGlobal, 8);
        panelRecursos.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        panelRecursos.Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1");
        
        // Coloreamos el fondo de este panel derecho sutilmente para diferenciarlo
        panelRecursos.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#f8fafc")); 

        // Líneas separadoras internas para los recursos (suaves)
        worksheet.Range(4, 6, filaRecursos - 1, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        worksheet.Range(4, 6, filaRecursos - 1, 8).Style.Border.InsideBorderColor = XLColor.FromHtml("#e2e8f0");


        // --- 6. AJUSTE DE COLUMNAS ---
        // Columna separadora E (5)
        worksheet.Column(5).Width = 3; 

        // Auto-ajustamos y damos "aire" (Padding)
        worksheet.Columns(1, 4).AdjustToContents();
        worksheet.Columns(6, 8).AdjustToContents();

        foreach (var col in worksheet.Columns(1, 4)) col.Width += 3;
        foreach (var col in worksheet.Columns(6, 8)) col.Width += 4; // Los links necesitan buen espacio

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
