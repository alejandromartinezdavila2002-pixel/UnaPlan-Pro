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

        // --- 1. ENCABEZADO (MÁS ALTO) ---
        var filaTitulo = worksheet.Row(1);
        filaTitulo.Height = 40; // Le damos altura premium
        
        worksheet.Cell("A1").Value = "PLAN DE EVALUACIÓN PERSONALIZADO - UNA";
        var titulo = worksheet.Range("A1:D1");
        titulo.Merge().Style
            .Font.SetBold()
            .Font.SetFontSize(18)
            .Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1e293b")) 
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // --- 2. CABECERAS (CON AIRE) ---
        worksheet.Row(3).Height = 25; // Altura para las cabeceras
        string[] headers = { "CÓDIGO", "MATERIA", "EVALUACIÓN", "FECHA DE ENTREGA" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(3, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.FromHtml("#f1f5f9")) // Gris muy claro moderno
                .Font.SetFontColor(XLColor.FromHtml("#475569"))
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1");
        }

        // --- 3. CUERPO (LOGICA ESPACIOSA) ---
        int filaActual = 4;
        var gruposPorMateria = materias.GroupBy(m => m.Codigo);

        foreach (var grupo in gruposPorMateria)
        {
            int filaInicioMateria = filaActual;

            foreach (var eval in grupo)
            {
                worksheet.Row(filaActual).Height = 22; // Filas de datos con más altura
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
            rangoNombre.Style.Alignment.SetIndent(1); // Pequeño margen a la izquierda del texto

            // Borde exterior de la materia completa (más fuerte)
            var rangoMateriaCompleta = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 4);
            rangoMateriaCompleta.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            rangoMateriaCompleta.Style.Border.OutsideBorderColor = XLColor.FromHtml("#94a3b8");
            
            filaActual++; // Dejamos una fila pequeña de separación entre materias
            worksheet.Row(filaActual-1).Height = 8; 
        }

        // --- 4. RECURSOS (LINKS) CON MUCHO AIRE ---
        filaActual += 3; // Espacio generoso antes de los links
        
        worksheet.Row(filaActual).Height = 30;
        worksheet.Cell(filaActual, 1).Value = "RECURSOS Y ENLACES OFICIALES";
        worksheet.Range(filaActual, 1, filaActual, 4).Merge().Style
            .Font.SetBold()
            .Font.SetFontSize(13)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#eff6ff")) 
            .Font.SetFontColor(XLColor.FromHtml("#1d4ed8"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        filaActual += 2;
        foreach (var mat in materias.DistinctBy(m => m.Codigo))
        {
            worksheet.Row(filaActual).Height = 20;
            worksheet.Cell(filaActual, 1).Value = $"Materia {mat.Codigo}:";
            worksheet.Cell(filaActual, 1).Style.Font.SetBold();

            var linkPlan = worksheet.Cell(filaActual, 2);
            linkPlan.Value = "📄 Plan de Curso";
            if (!string.IsNullOrEmpty(mat.UrlPlan))
            {
                string urlSeguraPlan = Uri.EscapeDataString(mat.UrlPlan);
                string urlMagicaPlan = $"https://unaplanapi.onrender.com/api/go?target={urlSeguraPlan}";
                linkPlan.SetHyperlink(new XLHyperlink(urlMagicaPlan));
            }
            linkPlan.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();

            if (mat.UrlsMateriales.Any() && !string.IsNullOrEmpty(mat.UrlsMateriales.First()))
            {
                var linkMat = worksheet.Cell(filaActual, 3);
                linkMat.Value = "📁 Carpeta de Apoyo";
                string urlSeguraMat = Uri.EscapeDataString(mat.UrlsMateriales.First());
                string urlMagicaMat = $"https://unaplanapi.onrender.com/api/go?target={urlSeguraMat}";
                linkMat.SetHyperlink(new XLHyperlink(urlMagicaMat));
                linkMat.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }
            
            filaActual++;
            worksheet.Row(filaActual).Height = 5; // Separador sutil
            filaActual++;
        }

        // Ajuste final de columnas con un margen extra
        worksheet.Columns().AdjustToContents();
        foreach (var col in worksheet.Columns(1, 4))
        {
            col.Width += 3; // Le sumamos un poquito más de ancho para que no pegue a los bordes
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
