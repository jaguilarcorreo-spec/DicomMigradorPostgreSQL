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
/// cualquier worker). Si <see cref="Threshold"/> o más fueron «sin instancias» de estudios
/// DISTINTOS, el problema es del origen, no de los estudios: el estudio se aparta sin gastar
/// intento (a «RetryPending», que se toma al final de la cola) y cuenta como fallo transitorio,
/// que lleva a la auto-pausa con reanudaciones cada vez más espaciadas. Un estudio que de verdad
/// no se envía, con el origen funcionando bien, sigue gastando sus intentos y acaba «Failed».
///
/// Estudios distintos: antes un mismo estudio que el origen no enviaba se devolvía a la cola,
/// otro worker lo tomaba al instante y en segundos llenaba él solo la ventana («10 de los
/// últimos 10»): la migración se pausaba y reanudaba sin fin por un único estudio (producción,
/// 28/10/2026). Ahora un estudio cuenta una sola vez mientras siga en la ventana.
///
/// En memoria: un reinicio del servicio empieza de cero.
/// </summary>
public static class OriginEmptyMoves
{
    public const int Window    = 10;
    public const int Threshold = 6;

    /// <summary>Por migración: últimos resultados (null = el origen envió; UID = estudio sin envío).</summary>
    private static readonly ConcurrentDictionary<int, Queue<string?>> _recent = new();

    /// <summary>El origen envió algo (estudio migrado, o parcial): no es un episodio.</summary>
    public static void RecordSent(int migrationId) => Push(migrationId, null);

    /// <summary>Registra un C-MOVE sin instancias del estudio <paramref name="studyUid"/>. True si,
    /// con él, la migración está en un episodio del origen; <paramref name="empties"/> = cuántos
    /// estudios distintos de los últimos resultados fueron sin instancias.</summary>
    public static bool RecordEmpty(int migrationId, string studyUid, out int empties)
    {
        empties = Push(migrationId, studyUid);
        return empties >= Threshold;
    }

    private static int Push(int migrationId, string? emptyStudyUid)
    {
        var q = _recent.GetOrAdd(migrationId, _ => new Queue<string?>(Window + 1));
        lock (q)
        {
            // Un estudio que ya está en la ventana como «sin envío» no vuelve a contar.
            if (emptyStudyUid is null || !q.Contains(emptyStudyUid))
            {
                q.Enqueue(emptyStudyUid);
                while (q.Count > Window) q.Dequeue();
            }
            return q.Count(e => e is not null);
        }
    }

    /// <summary>Olvida el historial (pruebas, o migración borrada).</summary>
    public static void Reset(int migrationId) => _recent.TryRemove(migrationId, out _);
}
