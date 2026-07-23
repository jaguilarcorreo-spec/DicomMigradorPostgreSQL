namespace DicomMigrator.Web.Components.Shared;

/// <summary>
/// Traducción a castellano de los estados para MOSTRARLOS en la interfaz.
///
/// IMPORTANTE: los valores en inglés ("Running", "Idle", "Verified"…) son los que se
/// almacenan en base de datos y con los que compara toda la lógica. Aquí solo se
/// traduce el texto visible; nunca se traduce el valor guardado.
///
/// Por el mismo motivo, las clases CSS de los badges (.s-Running, .s-Verified…) se
/// siguen construyendo con el valor CRUDO, no con la traducción.
/// </summary>
public static class StatusText
{
    /// <summary>Devuelve el texto en castellano de un estado. Si el valor no está en
    /// la tabla se devuelve tal cual: preferible mostrar el original que ocultarlo.</summary>
    public static string Es(string? status) => status switch
    {
        // ── Procesos (job de descubrimiento, migración, verificación, enumeración) ──
        "Draft"                => "Borrador",
        "Idle"                 => "Inactivo",
        "Running"              => "En ejecución",
        "Paused"               => "Pausado",
        "Completed"            => "Completado",
        "Cancelled"            => "Cancelado",

        // ── Estudios ──
        "Pending"              => "Pendiente",
        "Migrating"            => "Migrando",
        "Migrated"             => "Migrado",
        "RetryPending"         => "Reintento pendiente",
        "VerificationPending"  => "Verificando",
        "Verified"             => "Verificado",
        "VerifyRetryPending"   => "Reintento verificación",
        "VerifyFailed"         => "Verificación fallida",

        // ── Particiones ──
        "Queued"               => "En cola",
        "Ready"                => "Lista",
        "Subdivided"           => "Subdividida",
        "PossiblyTruncated"    => "Posible truncamiento",

        // "Failed" es común a procesos, estudios y particiones.
        "Failed"               => "Fallido",

        _                      => status ?? string.Empty,
    };
}
