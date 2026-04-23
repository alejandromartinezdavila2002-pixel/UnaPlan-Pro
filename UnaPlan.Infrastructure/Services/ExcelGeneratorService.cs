using ClosedXML.Excel;
using UnaPlan.Core.Entities;

namespace UnaPlan.Infrastructure.Services;

public class ExcelGeneratorService
{
    public class MateriaPlanillaDto
    {
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = ""; // Asegúrate de pasar el nombre real aquí
        public string TipoEvaluacion { get; set; } = "";
        public string FechaEntrega { get; set; } = "";
        public string UrlPlan { get; set; } = "";
        public List<string> UrlsMateriales { get; set; } = new();
    }

    public byte[] GenerarPlanDeEvaluacionExcel(List<MateriaPlanillaDto> materias)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Mi Plan UNA");

        // --- 1. ENCABEZADO ---
        worksheet.Cell("A1").Value = "PLAN DE EVALUACIÓN PERSONALIZADO - UNA";
        var titulo = worksheet.Range("A1:D1");
        titulo.Merge().Style
            .Font.SetBold()
            .Font.SetFontSize(16)
            .Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1e293b")) // Gris oscuro premium
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // --- 2. CABECERAS ---
        string[] headers = { "CÓDIGO", "MATERIA", "EVALUACIÓN", "FECHA DE ENTREGA" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(3, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        // --- 3. CUERPO CON FUSIÓN DE CELDAS (LOGICA INTELIGENTE) ---
        int filaActual = 4;
        var gruposPorMateria = materias.GroupBy(m => m.Codigo);

        foreach (var grupo in gruposPorMateria)
        {
            int filaInicioMateria = filaActual;
            int cantidadEvaluaciones = grupo.Count();

            foreach (var eval in grupo)
            {
                // Solo llenamos Tipo y Fecha (lo que cambia por fila)
                worksheet.Cell(filaActual, 3).Value = eval.TipoEvaluacion;
                worksheet.Cell(filaActual, 4).Value = eval.FechaEntrega;
                worksheet.Cell(filaActual, 4).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                filaActual++;
            }

            // FUSIONAMOS Código y Nombre para que abarquen todas sus evaluaciones
            var primerItem = grupo.First();

            // Celda Código
            var rangoCodigo = worksheet.Range(filaInicioMateria, 1, filaActual - 1, 1);
            rangoCodigo.Merge().Value = primerItem.Codigo;
            rangoCodigo.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center)
                               .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Celda Nombre (Limpiamos el nombre por si trae el .pdf)
            var rangoNombre = worksheet.Range(filaInicioMateria, 2, filaActual - 1, 2);
            rangoNombre.Merge().Value = primerItem.Nombre.Replace(".pdf", "").Replace(".PDF", "");
            rangoNombre.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center)
                               .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Borde separador entre materias
            worksheet.Range(filaInicioMateria, 1, filaActual - 1, 4).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        // --- 4. RECURSOS (LINKS) ---
        filaActual += 2;
        worksheet.Cell(filaActual, 1).Value = "RECURSOS Y ENLACES OFICIALES";
        worksheet.Range(filaActual, 1, filaActual, 4).Merge().Style
            .Font.SetBold()
            .Fill.SetBackgroundColor(XLColor.FromHtml("#eff6ff")) // Azul muy claro
            .Font.SetFontColor(XLColor.FromHtml("#1d4ed8"));

        filaActual++;
        foreach (var mat in materias.DistinctBy(m => m.Codigo))
        {
            worksheet.Cell(filaActual, 1).Value = $"Plan {mat.Codigo}:";
            var linkPlan = worksheet.Cell(filaActual, 2);
            linkPlan.Value = "Descargar Plan de Curso";
            linkPlan.SetHyperlink(new XLHyperlink(mat.UrlPlan));
            linkPlan.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();

            if (mat.UrlsMateriales.Any())
            {
                filaActual++;
                worksheet.Cell(filaActual, 1).Value = "Material:";
                var linkMat = worksheet.Cell(filaActual, 2);
                linkMat.Value = "Abrir Carpeta de Apoyo";
                linkMat.SetHyperlink(new XLHyperlink(mat.UrlsMateriales.First()));
                linkMat.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
            }
            filaActual += 2;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}