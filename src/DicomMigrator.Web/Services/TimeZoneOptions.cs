namespace DicomMigrator.Web.Services;

/// <summary>
/// Zonas horarias de Windows para el desplegable de la pestaña Ventana de una migración.
///
/// Se guardan con su identificador de Windows («Romance Standard Time»), que es el que
/// entiende TimeZoneInfo en el servicio. Las ventanas guardadas antes con un nombre IANA
/// («Europe/Madrid», el valor por defecto de entonces) siguen funcionando igual —.NET
/// convierte uno en otro— y en el desplegable aparecen ya convertidas a su zona de Windows.
/// </summary>
public static class TimeZoneOptions
{
    public sealed record Option(string Id, string Display);

    /// <summary>Todas las zonas del sistema, ordenadas por desfase UTC (como en Windows).</summary>
    public static IReadOnlyList<Option> All { get; } =
        TimeZoneInfo.GetSystemTimeZones().Select(z => new Option(z.Id, z.DisplayName)).ToList();

    /// <summary>Zona por defecto de una migración sin ventanas: la del sistema operativo en
    /// el que corre el servicio (o la de Madrid si no se reconoce). Una vez guardada una
    /// ventana, manda la zona elegida en el migrador, aunque cambie la del servidor.</summary>
    public static string Default { get; } =
        All.Any(o => o.Id == TimeZoneInfo.Local.Id) ? TimeZoneInfo.Local.Id : "Romance Standard Time";

    /// <summary>Identificador de Windows equivalente al guardado (IANA o Windows). Si no se
    /// reconoce, lo devuelve tal cual para que se vea y se pueda corregir.</summary>
    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Default;
        if (All.Any(o => o.Id == id)) return id;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) && All.Any(o => o.Id == win) ? win : id;
    }

    public static bool IsKnown(string? id) => id is not null && All.Any(o => o.Id == id);

    /// <summary>Nombre para mostrar («(UTC+01:00) Bruselas, Copenhague, Madrid, París»).</summary>
    public static string DisplayOf(string? id)
    {
        var norm = Normalize(id);
        return All.FirstOrDefault(o => o.Id == norm)?.Display ?? id ?? "";
    }
}
