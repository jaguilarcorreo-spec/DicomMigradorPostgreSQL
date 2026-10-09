using System.Security.Claims;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Cambio de contraseña obligatorio (contraseña inicial o reseteada por un administrador).
/// Mientras esté pendiente, el usuario solo puede cambiarla o cerrar sesión (UI-17, SEC-5).
///
/// Antes la regla solo estaba en MainLayout y buscaba "/cambiar-password" en la URL
/// completa, parámetros incluidos: con /?x=/cambiar-password se usaba toda la aplicación
/// sin cambiarla, y las descargas de CSV/Excel (datos de paciente) tampoco lo exigían.
/// Ahora se aplica en el servidor a todas las peticiones (filtro), en las exportaciones
/// (política) y en la navegación interna de Blazor (MainLayout), comparando la ruta exacta.
/// </summary>
public static class PendingPasswordChange
{
    public const string ClaimType = "must_change_pwd";
    public const string PagePath  = "/cambiar-password";

    /// <summary>Política de autorización: sesión iniciada y sin cambio de contraseña pendiente.</summary>
    public const string PasswordChangedPolicy = "PasswordChanged";

    public static bool IsPending(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.HasClaim(ClaimType, "1");

    /// <summary>True si la ruta (sin parámetros) es la página de cambio de contraseña.</summary>
    public static bool IsChangePasswordPath(string? path) =>
        string.Equals(path?.TrimEnd('/'), PagePath, StringComparison.OrdinalIgnoreCase);

    // Ficheros estáticos que necesitan el login y la página de cambio para pintarse.
    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".webmanifest",
    };

    private static bool IsAllowed(PathString path)
    {
        var p = path.Value ?? "/";
        if (IsChangePasswordPath(p)) return true;
        if (p.Equals("/login", StringComparison.OrdinalIgnoreCase)
         || p.Equals("/logout", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
         || p.StartsWith("/_content", StringComparison.OrdinalIgnoreCase)
         || p.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)) return true;
        return StaticExtensions.Contains(Path.GetExtension(p));
    }

    /// <summary>Filtro en el servidor: con el cambio pendiente, cualquier otra página
    /// redirige a /cambiar-password; descargas y otras operaciones reciben 403.</summary>
    public static IApplicationBuilder UsePendingPasswordChangeGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (IsPending(ctx.User) && !IsAllowed(ctx.Request.Path))
            {
                var ext = Path.GetExtension(ctx.Request.Path.Value ?? "");
                if (HttpMethods.IsGet(ctx.Request.Method)
                    && !ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                    ctx.Response.Redirect(PagePath);
                else
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
}
