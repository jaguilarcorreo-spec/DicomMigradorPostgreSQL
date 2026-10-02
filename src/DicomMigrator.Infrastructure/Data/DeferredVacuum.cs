using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// VACUUM (ANALYZE) diferido tras borrados masivos (borrar una migración, borrar o
/// resetear un job de descubrimiento). Sin él, en el ciclo típico borrar → repoblar las
/// claves nuevas (Ids crecientes) caen siempre en páginas de índice nuevas, porque las
/// vaciadas por el DELETE no son reutilizables hasta que pasa VACUUM, y los índices
/// crecen sin límite (llegó a verse 100× el tamaño necesario).
///
/// Se ejecuta en segundo plano para no hacer esperar a la UI, con una pequeña espera
/// que agrupa varias peticiones seguidas en una sola pasada. Singleton; best-effort.
/// </summary>
public sealed class DeferredVacuum(IServiceScopeFactory scopeFactory, ILogger<DeferredVacuum> logger)
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private int _running;   // 0/1: hay una pasada en curso o programada

    /// <summary>Encola las tablas para un VACUUM (ANALYZE) en segundo plano.</summary>
    public void Request(params string[] tables)
    {
        foreach (var t in tables) _pending[t] = 0;
        TryStart();
    }

    private void TryStart()
    {
        if (_pending.IsEmpty || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(Debounce);
            var tables = _pending.Keys.ToList();
            foreach (var t in tables) _pending.TryRemove(t, out _);

            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseMaintenance>()
                .VacuumTablesAsync(tables);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VACUUM diferido falló (no crítico).");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            TryStart();   // peticiones llegadas durante la pasada
        }
    }
}
