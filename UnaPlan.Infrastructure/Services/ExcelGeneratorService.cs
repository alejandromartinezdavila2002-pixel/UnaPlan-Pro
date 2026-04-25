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

        // --- 1. CONFIGURACIÓN DE VISTA (ZOOM) ---
        worksheet.SheetView.ZoomScale = 125;
        worksheet.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        // --- 2. ENCABEZADO MAESTRO (FILA 1) ---
        worksheet.Row(1).Height = 45; 
        worksheet.Cell("A1").Value = "PLAN DE EVALUACIÓN PERSONALIZADO - UNA";
        var tituloMaestro = worksheet.Range("A1:H1");
        tituloMaestro.Merge().Style
            .Font.SetBold()
            .Font.SetFontSize(20)
            .Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1e293b"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // --- 3. LABELS PRINCIPALES DE SECCIÓN (FILA 2) ---
        worksheet.Row(2).Height = 30;

        // Label Tabla 1 (Izquierda)
        var labelTabla1 = worksheet.Range("A2:D2");
        labelTabla1.Merge().Value = "📅 CRONOGRAMA DE ENTREGAS OFICIALES";
        labelTabla1.Style
            .Font.SetBold()
            .Font.SetFontSize(13)
            .Font.SetFontColor(XLColor.FromHtml("#1e293b"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
            .Border.OutsideBorder = XLBorderStyleValues.Medium;

        // Label Tabla 2 (Derecha)
        var labelTabla2 = worksheet.Range("F2:H2");
        labelTabla2.Merge().Value = "🔗 RECURSOS Y ENLACES ACADÉMICOS";
        labelTabla2.Style
            .Font.SetBold()
            .Font.SetFontSize(13)
            .Font.SetFontColor(XLColor.FromHtml("#1d4ed8"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
            .Border.OutsideBorder = XLBorderStyleValues.Medium;


        // --- 4. CABECERAS DE COLUMNAS SIMÉTRICAS (FILA 4) ---
        worksheet.Row(3).Height = 10; // Respiro visual
        int filaInicioDatos = 5;
        
        // Cabeceras Tabla Principal (Izquierda)
        string[] headersIzq = { "CÓDIGO", "MATERIA", "EVALUACIÓN", "FECHA DE ENTREGA" };
        for (int i = 0; i < headersIzq.Length; i++)
        {
            var cell = worksheet.Cell(4, i + 1);
            cell.Value = headersIzq[i];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.FromHtml("#f1f5f9"))
                .Font.SetFontColor(XLColor.FromHtml("#475569"))
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                .Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        // Cabeceras Tabla Recursos (Derecha) - ¡NUEVO!
        string[] headersDer = { "MATERIA", "DOC. OFICIAL", "MATERIAL EXTRA" };
        for (int i = 0; i < headersDer.Length; i++)
        {
            var cell = worksheet.Cell(4, i + 6);
            cell.Value = headersDer[i];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.FromHtml("#f1f5f9"))
                .Font.SetFontColor(XLColor.FromHtml("#475569"))
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                .Border.OutsideBorder = XLBorderStyleValues.Thin;
        }


        // --- 5. CONSTRUIR TABLA PRINCIPAL (IZQUIERDA) ---
        int filaActual = filaInicioDatos;
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
                
                worksheet.Range(filaActual, 3, filaActual, 4).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                worksheet.Range(filaActual, 3, filaActual, 4).Style.Border.OutsideBorderColor = XLColor.FromHtml("#e2e8f0");
                filaActual++;
            }

            var primerItem = grupo.First();
            var rangoCodigo = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 1);
            rangoCodigo.Merge().Value = primerItem.Codigo;
            rangoCodigo.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var rangoNombre = worksheet.Range(filaInicioMateria, 2, filaActual - 1, 2);
            rangoNombre.Merge().Value = primerItem.Nombre.Replace(".pdf", "", StringComparison.OrdinalIgnoreCase).Trim();
            rangoNombre.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left).Alignment.SetIndent(1);

            var rangoMateriaCompleta = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 4);
            rangoMateriaCompleta.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            rangoMateriaCompleta.Style.Border.OutsideBorderColor = XLColor.FromHtml("#94a3b8");

            filaActual++; 
            worksheet.Row(filaActual - 1).Height = 8;
        }


        // --- 6. CONSTRUIR TABLA RECURSOS (DERECHA) ---
        int filaRecursos = filaInicioDatos;
        
        foreach (var mat in materias.DistinctBy(m => m.Codigo))
        {
            worksheet.Row(filaRecursos).Height = 22;
            worksheet.Cell(filaRecursos, 6).Value = $"Materia {mat.Codigo}";
            worksheet.Cell(filaRecursos, 6).Style.Font.SetBold().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var linkPlan = worksheet.Cell(filaRecursos, 7);
            if (!string.IsNullOrEmpty(mat.UrlPlan))
            {
                linkPlan.Value = "📄 Plan de Curso";
                string urlMagicaPlan = $"https://unaplanapi.onrender.com/api/go?target={Uri.EscapeDataString(mat.UrlPlan)}";
                linkPlan.SetHyperlink(new XLHyperlink(urlMagicaPlan));
                linkPlan.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }

            var linkMat = worksheet.Cell(filaRecursos, 8);
            if (mat.UrlsMateriales.Any() && !string.IsNullOrEmpty(mat.UrlsMateriales.First()))
            {
                linkMat.Value = "📁 Carpeta de Apoyo";
                string urlMagicaMat = $"https://unaplanapi.onrender.com/api/go?target={Uri.EscapeDataString(mat.UrlsMateriales.First())}";
                linkMat.SetHyperlink(new XLHyperlink(urlMagicaMat));
                linkMat.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }
            filaRecursos++;
        }


        // --- 7. AJUSTES FINALES Y SIMETRÍA ---
        int ultimaFilaGlobal = Math.Max(filaActual - 1, filaRecursos - 1);

        // Panel de recursos con fondo sutil (¡AHORA EMPIEZA EN LA FILA 4 PARA IGUALAR A LA OTRA!)
        var panelRecursos = worksheet.Range(4, 6, ultimaFilaGlobal, 8);
        panelRecursos.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        panelRecursos.Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1");
        panelRecursos.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#f8fafc")); 

        // Bordes internos sutiles para la zona de datos de los recursos
        if(filaRecursos > filaInicioDatos)
        {
            worksheet.Range(filaInicioDatos, 6, filaRecursos - 1, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            worksheet.Range(filaInicioDatos, 6, filaRecursos - 1, 8).Style.Border.InsideBorderColor = XLColor.FromHtml("#e2e8f0");
        }

        // Columna separadora E
        worksheet.Column(5).Width = 4; 

        // Auto-ajuste de columnas
        worksheet.Columns(1, 4).AdjustToContents();
        worksheet.Columns(6, 8).AdjustToContents();
        foreach (var col in worksheet.Columns(1, 8)) col.Width += 4;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
