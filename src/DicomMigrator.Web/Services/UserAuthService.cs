using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.AspNetCore.Identity;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Autenticación de usuarios locales: verificación de contraseña, limitación de
/// intentos fallidos (<see cref="LoginThrottle"/>) y cambio de contraseña.
///
/// Se usa <see cref="PasswordHasher{T}"/> de ASP.NET Core (PBKDF2 con sal por
/// usuario), disponible en el framework compartido sin dependencias añadidas.
/// No se guarda la contraseña en claro en ningún punto.
/// </summary>
public class UserAuthService(IUserRepository users, LoginThrottle throttle, ILogger<UserAuthService> logger)
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    /// <summary>Hash de una contraseña cualquiera, para comprobar contra él cuando el
    /// usuario no existe: así tarda lo mismo (PBKDF2) que con un usuario real y el
    /// tiempo de respuesta no delata qué cuentas existen (SEC-6).</summary>
    private static readonly string DummyHash =
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));

    public string Hash(AppUser user, string password) => _hasher.HashPassword(user, password);

    /// <summary>
    /// Valida las credenciales. Devuelve el usuario si son correctas, o un mensaje
    /// de error apto para mostrar.
    ///
    /// Ninguna respuesta revela si el usuario existe (SEC-6): usuario inexistente y
    /// contraseña errónea dan el mismo mensaje y tardan lo mismo; "demasiados intentos"
    /// se aplica igual a nombres inexistentes; y "cuenta desactivada" solo se dice a quien
    /// ha acertado la contraseña. <paramref name="clientIp"/> es la IP del navegador, para
    /// limitar los intentos por equipo y no bloquear al dueño de la cuenta.
    /// </summary>
    public async Task<(AppUser? User, string? Error)> ValidateAsync(string userName, string password, string? clientIp)
    {
        const string generic = "Usuario o contraseña incorrectos.";

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return (null, generic);

        var name = userName.Trim().ToLowerInvariant();
        var ip   = string.IsNullOrEmpty(clientIp) ? "?" : clientIp;

        if (!throttle.TryBegin(name, ip, out var wait))
        {
            logger.LogWarning("Acceso rechazado por exceso de intentos fallidos: usuario {User} desde {Ip}.", name, ip);
            return (null, TooManyAttempts(wait));
        }

        var user = await users.GetByUserNameAsync(name);
        if (user is null)
        {
            _hasher.VerifyHashedPassword(new AppUser(), DummyHash, password);   // mismo coste
            logger.LogWarning("Intento de acceso con usuario inexistente: {User} desde {Ip}", userName, ip);
            return (null, generic);
        }

        // Se comprueba la contraseña siempre, también con la cuenta bloqueada, para que el
        // tiempo de respuesta sea el mismo en todos los casos.
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        // Bloqueo de cuenta guardado en la BD (fallos desde varios equipos; sobrevive a
        // un reinicio). Mismo mensaje que el límite por equipo, acierte o no.
        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
            return (null, TooManyAttempts(until - DateTime.UtcNow));

        if (result == PasswordVerificationResult.Failed)
        {
            DateTime? lockUntil = null;
            if (throttle.AccountExhausted(name, out var accountUntil))
            {
                lockUntil = accountUntil;
                logger.LogWarning("Usuario {User} bloqueado hasta {Until:HH:mm} UTC: {N} intentos fallidos desde " +
                                  "uno o varios equipos en {Min} min.", user.UserName, accountUntil,
                                  throttle.MaxPerAccount, (int)throttle.Period.TotalMinutes);
            }
            else
                logger.LogWarning("Contraseña incorrecta para {User} desde {Ip}.", user.UserName, ip);
            await users.RecordFailedLoginAsync(user.Id, lockUntil);
            return (null, generic);
        }

        // Contraseña correcta: a partir de aquí ya se puede decir el estado real.
        throttle.Succeeded(name, ip);

        if (!user.IsActive)
        {
            logger.LogWarning("Intento de acceso de usuario desactivado: {User}", user.UserName);
            return (null, "Esta cuenta está desactivada. Contacta con el administrador.");
        }

        // Éxito: si el hash usaba parámetros antiguos, se regenera de forma transparente.
        string? rehash = null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = rehash = Hash(user, password);

        await users.RecordSuccessfulLoginAsync(user.Id, rehash);

        logger.LogInformation("Acceso correcto: {User} ({Role}) desde {Ip}", user.UserName, user.Role, ip);
        return (user, null);
    }

    private static string TooManyAttempts(TimeSpan wait)
    {
        var mins = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Demasiados intentos fallidos. Inténtalo de nuevo en {mins} minuto(s).";
    }

    /// <summary>Cambia la contraseña comprobando antes la actual.</summary>
    public async Task<string?> ChangePasswordAsync(string userName, string current, string nuevo, string repeat)
    {
        if (nuevo != repeat)             return "La nueva contraseña y su repetición no coinciden.";
        if (string.IsNullOrEmpty(nuevo)) return "La nueva contraseña no puede estar vacía.";
        if (nuevo.Length < 8)            return "La nueva contraseña debe tener al menos 8 caracteres.";
        if (nuevo == current)            return "La nueva contraseña debe ser distinta de la actual.";

        var user = await users.GetByUserNameAsync(userName);
        if (user is null) return "Usuario no encontrado.";

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed)
            return "La contraseña actual no es correcta.";

        user.PasswordHash       = Hash(user, nuevo);
        user.MustChangePassword = false;
        await users.UpdateAsync(user);

        logger.LogInformation("Contraseña cambiada para {User}.", user.UserName);
        return null;
    }
}
