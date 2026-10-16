using System.Collections.Concurrent;

namespace DicomMigrator.Infrastructure.Services.Migration;

/// <summary>
/// Episodios en los que el PACS origen responde al C-MOVE «terminado» pero no envía ninguna
/// imagen (Completed = 0), por migración.
///
/// Visto en producción: un origen que funciona horas sin un fallo y, de golpe, durante 10-50
/// minutos (tareas programadas del PACS, almacenamiento ocupado…), contesta así a casi todos
/// los estudios; después vuelve a funcionar y esos mismos estudios se migran bien. Como cada
/// intento fallaba en 1-2 s y el reintento llegaba al minuto, un estudio gastaba sus 3 intentos
/// dentro del mismo episodio: en una noche, 2.297 estudios acabaron «Failed» estando bien.
///
/// Se recuerdan los últimos <see cref="Window"/> resultados de C-MOVE de la migración (de
/// cualquier worker). Si <see cref="Threshold"/> o más fueron «sin instancias», el problema
/// es del origen, no de los estudios: el estudio vuelve a la cola sin gastar intento y cuenta
/// como fallo transitorio, que lleva a la auto-pausa con reanudaciones cada vez más espaciadas.
/// Un estudio que de verdad no se envía, con el origen funcionando bien, sigue gastando sus
/// intentos y acaba «Failed».
///
/// En memoria: un reinicio del servicio empieza de cero.
/// </summary>
public static class OriginEmptyMoves
{
    public const int Window    = 10;
    public const int Threshold = 6;

    private static readonly ConcurrentDictionary<int, Queue<bool>> _recent = new();

    /// <summary>El origen envió algo (estudio migrado, o parcial): no es un episodio.</summary>
    public static void RecordSent(int migrationId) => Push(migrationId, empty: false);

    /// <summary>Registra un C-MOVE sin instancias. True si, con él, la migración está en un
    /// episodio del origen; <paramref name="empties"/> = cuántos de los últimos resultados
    /// fueron sin instancias.</summary>
    public static bool RecordEmpty(int migrationId, out int empties)
    {
        empties = Push(migrationId, empty: true);
        return empties >= Threshold;
    }

    private static int Push(int migrationId, bool empty)
    {
        var q = _recent.GetOrAdd(migrationId, _ => new Queue<bool>(Window + 1));
        lock (q)
        {
            q.Enqueue(empty);
            while (q.Count > Window) q.Dequeue();
            return q.Count(e => e);
        }
    }

    /// <summary>Olvida el historial (pruebas, o migración borrada).</summary>
    public static void Reset(int migrationId) => _recent.TryRemove(migrationId, out _);
}
