using DicomMigrator.Core.Models;

namespace DicomMigrator.Infrastructure.Services.Licensing;

/// <summary>Opciones de licencia resueltas desde configuración (Web) y pasadas a
/// Infrastructure sin acoplarla a IConfiguration.</summary>
public sealed class LicenseOptions
{
    /// <summary>Ruta a un fichero .dmlic para importar en el primer arranque si la BD
    /// no tiene token instalado. Configuración: License:Path. Opcional.</summary>
    public string? FilePath { get; set; }
}

/// <summary>Caché singleton de la última evaluación de licencia. Lecturas baratas
/// desde la UI y verja de las migraciones. Escritura solo desde LicenseService.</summary>
public sealed class LicenseStatusCache
{
    private volatile LicenseStatusSnapshot _current = LicenseStatusSnapshot.Unknown();

    public LicenseStatusSnapshot Current => _current;

    public void Set(LicenseStatusSnapshot snapshot) => _current = snapshot;
}
