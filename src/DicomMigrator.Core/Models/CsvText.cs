namespace DicomMigrator.Core.Models;

/// <summary>
/// Escritura segura de campos CSV, común a todas las exportaciones (SEC-8).
///
/// Los valores vienen de PACS externos (nombre del paciente, descripción, errores…) y
/// no se controlan. Dos riesgos:
///   · Inyección de fórmulas: Excel ejecuta como fórmula una celda que empieza por
///     = + - @ (o tabulador / retorno de carro), p. ej. =HYPERLINK(…) o =cmd|…, al abrir
///     el CSV. Se neutraliza anteponiendo un apóstrofo, que Excel muestra como texto.
///   · Desplazamiento de columnas: comas, comillas o saltos de línea dentro del valor.
///     Se entrecomilla duplicando las comillas internas (RFC 4180).
/// Antes había tres copias del escapado, ninguna neutralizaba fórmulas y dos ignoraban
/// el retorno de carro.
/// </summary>
public static class CsvText
{
    /// <summary>Campo listo para escribir en una línea CSV separada por comas.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // OWASP "CSV Injection": prefijo ' si empieza por un carácter que dispara fórmula.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        var needsQuotes = value.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0
                       || value[0] == ' ' || value[^1] == ' ';
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
