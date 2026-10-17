namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// Eventos de la línea de tiempo de una migración (arranques, pausas, ventanas, licencia,
/// fin) en forma compacta, para la pestaña Rendimiento. Salen de AuditLogs, pero la purga
/// diaria borra el nivel INFO de más de 90 días; por eso se copian a
/// MigrationPerformance.Events al terminar la migración y antes de cada purga.
///
/// Cada evento es {"i": Id de auditoría, "t": milisegundos Unix (UTC), "k": código}. El
/// tipo de pausa se clasifica al escribir, así no hace falta guardar el mensaje:
/// S arranque · C fin · W cierre de ventana · O apertura · L verja de licencia ·
/// PA auto-pausa · PN auto-pausa por origen sin envío · PC pausa por configuración ·
/// PM pausa manual.
/// </summary>
public static class PerformanceEventsSql
{
    /// <summary>Acciones de auditoría que forman la línea de tiempo.</summary>
    public const string Actions = "'START', 'PAUSE', 'WINDOW_CLOSE', 'WINDOW_OPEN', 'LICENSE', 'COMPLETE'";

    private const string Code = @"CASE a.""Action""
           WHEN 'START' THEN 'S' WHEN 'COMPLETE' THEN 'C' WHEN 'WINDOW_CLOSE' THEN 'W'
           WHEN 'WINDOW_OPEN' THEN 'O' WHEN 'LICENSE' THEN 'L'
           ELSE CASE WHEN coalesce(a.""TechnicalMessage"", '') LIKE 'Auto-pausa%'
                     THEN CASE WHEN a.""TechnicalMessage"" LIKE '%no envía imágenes%' THEN 'PN' ELSE 'PA' END
                     WHEN coalesce(a.""TechnicalMessage"", '') LIKE '%error de configuración%' THEN 'PC'
                     ELSE 'PM' END
       END";

    /// <summary>Eventos (m, i, t, k) de las migraciones del CTE <c>mig(m)</c>: los guardados
    /// más los que sigan en la auditoría, sin repetir (si están en los dos, manda la auditoría).</summary>
    private const string Merged = $@"
ev AS (
    SELECT DISTINCT ON (u.m, u.i) u.m, u.i, u.t, u.k
    FROM (
        SELECT a.""MigrationId"" AS m, a.""Id"" AS i,
               floor(extract(epoch FROM a.""Timestamp"") * 1000)::bigint AS t,
               {Code} AS k, 0 AS src
        FROM ""AuditLogs"" a
        WHERE a.""MigrationId"" IN (SELECT m FROM mig) AND a.""Action"" IN ({Actions})
        UNION ALL
        SELECT p.""MigrationId"", x.i, x.t, x.k, 1
        FROM ""MigrationPerformance"" p
        CROSS JOIN LATERAL jsonb_to_recordset(p.""Events"") AS x(i bigint, t bigint, k text)
        WHERE p.""MigrationId"" IN (SELECT m FROM mig)
    ) u
    ORDER BY u.m, u.i, u.src
)";

    /// <summary>Lectura para la pestaña: eventos de la migración <c>@mig</c> ordenados.</summary>
    public const string Read = $@"
WITH mig(m) AS (SELECT @mig),
{Merged}
SELECT ev.t AS ""UnixMs"", ev.k AS ""Kind"" FROM ev ORDER BY ev.t, ev.i";

    /// <summary>Guarda (acumulando) los eventos de las migraciones que devuelve
    /// <paramref name="migrationsSql"/> (una columna). Una sola sentencia; las migraciones
    /// ya borradas se ignoran.</summary>
    public static string Save(string migrationsSql) => $@"
WITH mig(m) AS (SELECT g.""Id"" FROM ""Migrations"" g WHERE g.""Id"" IN ({migrationsSql})),
{Merged}
INSERT INTO ""MigrationPerformance"" (""MigrationId"", ""Events"", ""UpdatedAt"")
SELECT ev.m, jsonb_agg(jsonb_build_object('i', ev.i, 't', ev.t, 'k', ev.k) ORDER BY ev.t, ev.i), now()
FROM ev GROUP BY ev.m
ON CONFLICT (""MigrationId"") DO UPDATE SET ""Events"" = EXCLUDED.""Events"", ""UpdatedAt"" = EXCLUDED.""UpdatedAt""";
}
