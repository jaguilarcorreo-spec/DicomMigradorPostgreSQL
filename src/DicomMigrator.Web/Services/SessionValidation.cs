using System.Collections.Concurrent;
using System.Security.Claims;
using DicomMigrator.Core.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Comprueba que una sesión sigue siendo válida contra la base de datos (SEC-2).
///
/// La cookie de sesión dura 8 h y se renueva con el uso. Antes no se volvía a mirar la
/// BD: un usuario desactivado, con el rol rebajado o con la contraseña cambiada seguía
/// usando su sesión abierta, y una cookie copiada seguía valiendo tras cerrar sesión.
/// Ahora la cookie lleva el sello de seguridad del usuario (AppUser.SecurityStamp) y la
/// sesión solo vale si el usuario existe, está activo y su sello no ha cambiado.
/// </summary>
public static class SessionValidator
{
    /// <summary>Claim con el sello de seguridad del usuario.</summary>
    public const string StampClaim = "sstamp";

    // Resultado reciente por (usuario, sello). Cada petición HTTP, incluidos ficheros
    // estáticos, pasa por la validación de la cookie; así no se consulta la BD en cada
    // una. Un cambio en el usuario tarda como mucho CacheFor en surtir efecto.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<(int Id, string Stamp), (bool Valid, DateTime At)> _cache = new();

    public static async Task<bool> IsValidAsync(ClaimsPrincipal principal, IServiceProvider services,
        bool useCache = true)
    {
        if (principal.Identity?.IsAuthenticated != true) return true;   // anónimo: nada que validar

        // Las cookies emitidas antes de este cambio no traen sello: obliga a entrar de nuevo.
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        var stamp = principal.FindFirstValue(StampClaim);
        if (string.IsNullOrEmpty(stamp)) return false;

        var key = (id, stamp);
        if (useCache && _cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return hit.Valid;

        bool valid;
        try
        {
            var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(id);
            valid = user is { IsActive: true }
                 && user.SecurityStamp == stamp
                 && user.Role == principal.FindFirstValue(ClaimTypes.Role);
        }
        catch (Exception ex)
        {
            // BD caída momentáneamente: no se expulsa a nadie por eso (la aplicación no
            // funcionaría igualmente). Se revalidará en cuanto vuelva.
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionValidator))
                .LogWarning(ex, "No se pudo validar la sesión del usuario {Id} (se mantiene).", id);
            return true;
        }

        _cache[key] = (valid, DateTime.UtcNow);
        if (_cache.Count > 1000) _cache.Clear();   // tope de memoria; se recompone sola
        return valid;
    }
}

/// <summary>
/// Revalida periódicamente la sesión de los circuitos de Blazor Server ya abiertos
/// (SEC-2). Un circuito vive sobre una única conexión, sin peticiones HTTP nuevas que
/// vuelvan a pasar por la cookie, así que sin esto una pestaña abierta conservaría el
/// acceso durante horas tras desactivar al usuario. Si la sesión deja de ser válida, el
/// usuario pasa a anónimo y la interfaz lo manda al login.
///
/// Intervalo configurable en Auth:SessionRevalidationSeconds (por defecto 300 s, mínimo
/// 10 s): es el tiempo máximo que una pestaña ya abierta conserva el acceso tras
/// desactivar al usuario, cambiarle el rol o la contraseña, o cerrar su sesión. Las
/// páginas que se cargan de nuevo y las descargas se comprueban siempre al momento.
/// </summary>
public sealed class SessionRevalidatingAuthStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, IConfiguration configuration)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval { get; } =
        TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("Auth:SessionRevalidationSeconds", 300)));

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await SessionValidator.IsValidAsync(authenticationState.User, scope.ServiceProvider, useCache: false);
    }
}
