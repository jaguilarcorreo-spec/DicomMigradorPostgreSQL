using DicomMigrator.Core.Models;

namespace DicomMigrator.Core.Interfaces;

/// <summary>Calcula el fingerprint estable de la máquina donde corre la app.
/// Es el valor que el cliente obtiene con <c>DicomMigrator.exe --fingerprint</c> y
/// que el fabricante embebe en la licencia (campo machine_fp) para ligarla.</summary>
public interface IMachineFingerprintProvider
{
    string GetFingerprint();
}

/// <summary>Acceso a la fila única de estado de licencia en PostgreSQL.</summary>
public interface ILicenseStateRepository
{
    /// <summary>Devuelve la fila de estado (la crea si no existe).</summary>
    Task<LicenseState> GetAsync();
    Task SaveAsync(LicenseState state);
}

/// <summary>Servicio de licencias: evaluación al arrancar, instalación de un token
/// nuevo y consulta del estado cacheado.</summary>
public interface ILicenseService
{
    /// <summary>Estado cacheado de la última evaluación (lectura barata).</summary>
    LicenseStatusSnapshot Current { get; }

    /// <summary>Evalúa el token instalado (o lo importa de fichero si procede),
    /// aplica ventana temporal, binding de máquina y anti-rollback, actualiza el
    /// estado en BD y refresca la caché.</summary>
    Task<LicenseStatusSnapshot> EvaluateAsync(CancellationToken ct = default);

    /// <summary>Instala un token nuevo: lo valida y, si es utilizable y no supone un
    /// rollback, lo persiste como token activo. Devuelve (ok, error, foto resultante).</summary>
    Task<(bool Ok, string? Error, LicenseStatusSnapshot Snapshot)> InstallAsync(
        string token, string? actor = null, CancellationToken ct = default);
}
