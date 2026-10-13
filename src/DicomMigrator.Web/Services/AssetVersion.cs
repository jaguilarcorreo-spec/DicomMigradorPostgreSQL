using System.Collections.Concurrent;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Añade a la URL de un fichero estático (css/js) una marca que cambia con su contenido
/// («css/app.css?v=8d1c…»). Sin ella, tras actualizar la aplicación el navegador puede seguir
/// usando el fichero de su caché y la pantalla sale sin los estilos o el código nuevos.
/// La marca es la fecha de modificación del fichero; se calcula una vez por ruta.
/// </summary>
public static class AssetVersion
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Url(IWebHostEnvironment env, string path) => Cache.GetOrAdd(path, p =>
    {
        try
        {
            var full = Path.Combine(env.WebRootPath ?? "", p.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? $"{p}?v={File.GetLastWriteTimeUtc(full).Ticks:x}" : p;
        }
        catch { return p; }
    });
}
