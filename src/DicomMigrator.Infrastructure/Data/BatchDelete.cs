using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// Borrado por lotes de tablas grandes (DISC-13). Antes, borrar un job o una migración era
/// un único DELETE que arrastraba en cascada millones de instancias, con el límite por
/// defecto de 30 s por sentencia: con volumen real se cancelaba, se revertía entero y no
/// había forma de borrarlo desde la interfaz. Ahora se borra en tandas pequeñas, cada una en
/// su propia sentencia (y transacción) con un límite holgado: el trabajo hecho no se pierde
/// si algo falla a mitad, y repetir la operación continúa donde se quedó.
/// </summary>
public static class BatchDelete
{
    /// <summary>Estudios por tanda. Cada uno arrastra sus instancias (unas cientos de media),
    /// así que una tanda son del orden de cientos de miles de filas.</summary>
    public const int StudiesPerBatch = 1000;

    /// <summary>Filas por tanda en tablas sin hijos (auditoría, peticiones).</summary>
    public const int RowsPerBatch = 20000;

    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ejecuta en bucle <c>DELETE FROM {table} WHERE "Id" IN (SELECT "Id" FROM {table}
    /// WHERE {keyColumn} = @key LIMIT n)</c> hasta que no quede nada. Devuelve el total.
    /// Tabla y columna son constantes del código, nunca datos del usuario.
    /// </summary>
    public static async Task<long> RunAsync(IDbContextFactory<AppDbContext> factory,
        string table, string keyColumn, int key, int batchSize, IProgress<long>? progress = null)
    {
        var sql = $@"DELETE FROM ""{table}"" WHERE ""Id"" IN (
    SELECT ""Id"" FROM ""{table}"" WHERE ""{keyColumn}"" = {{0}} LIMIT {batchSize})";
        long total = 0;
        while (true)
        {
            await using var db = factory.CreateDbContext();
            db.Database.SetCommandTimeout(BatchTimeout);
            var n = await db.Database.ExecuteSqlRawAsync(sql, key);
            if (n == 0) return total;
            total += n;
            progress?.Report(total);
        }
    }
}
