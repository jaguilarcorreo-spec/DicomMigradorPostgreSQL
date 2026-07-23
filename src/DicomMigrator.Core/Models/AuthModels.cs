namespace DicomMigrator.Core.Models;

/// <summary>
/// Usuario local de la aplicación. El proveedor de identidad está aislado tras
/// IUserRepository + el servicio de autenticación, de modo que sustituirlo más
/// adelante por Active Directory u OIDC no obligue a rehacer la aplicación.
/// </summary>
public class AppUser
{
    public int      Id           { get; set; }

    /// <summary>Nombre de acceso. Único, se compara en minúsculas.</summary>
    public string   UserName     { get; set; } = string.Empty;

    /// <summary>Nombre visible en la interfaz y en la auditoría.</summary>
    public string?  DisplayName  { get; set; }

    /// <summary>Hash de la contraseña (PasswordHasher de ASP.NET Core).
    /// NUNCA se almacena la contraseña en claro.</summary>
    public string   PasswordHash { get; set; } = string.Empty;

    /// <summary>Administrador | Operador | Consulta — ver <see cref="AppRoles"/>.</summary>
    public string   Role         { get; set; } = AppRoles.Consulta;

    /// <summary>Un usuario desactivado no puede entrar, pero se conserva para que
    /// la auditoría histórica siga teniendo a quién atribuir las acciones.</summary>
    public bool     IsActive     { get; set; } = true;

    /// <summary>Obliga a cambiar la contraseña en el próximo acceso.</summary>
    public bool     MustChangePassword { get; set; }

    // ── Bloqueo por intentos fallidos ────────────────────────────────────────
    public int       FailedAttempts { get; set; }
    public DateTime? LockedUntil    { get; set; }

    public DateTime? LastLoginDate  { get; set; }
    public DateTime  CreatedDate    { get; set; } = DateTime.UtcNow;
}

/// <summary>Roles de la aplicación. Son cadenas fijas: se guardan en BD y se usan
/// en los claims, así que cambiarlas obliga a migrar los datos existentes.</summary>
public static class AppRoles
{
    /// <summary>Todo: nodos, borrado de jobs y migraciones, gestión de usuarios.</summary>
    public const string Administrador = "Administrador";

    /// <summary>Lanzar descubrimientos, migraciones y verificaciones; exportar.</summary>
    public const string Operador      = "Operador";

    /// <summary>Solo lectura: consultar y exportar. Nada que modifique.</summary>
    public const string Consulta      = "Consulta";

    public static readonly string[] All = [Administrador, Operador, Consulta];
}
