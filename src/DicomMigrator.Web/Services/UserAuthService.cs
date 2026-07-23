using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.AspNetCore.Identity;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Autenticación de usuarios locales: verificación de contraseña, bloqueo por
/// intentos fallidos y cambio de contraseña.
///
/// Se usa <see cref="PasswordHasher{T}"/> de ASP.NET Core (PBKDF2 con sal por
/// usuario), disponible en el framework compartido sin dependencias añadidas.
/// No se guarda la contraseña en claro en ningún punto.
/// </summary>
public class UserAuthService(IUserRepository users, ILogger<UserAuthService> logger)
{
    /// <summary>Intentos fallidos consecutivos antes de bloquear la cuenta.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>Duración del bloqueo tras agotar los intentos.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly PasswordHasher<AppUser> _hasher = new();

    public string Hash(AppUser user, string password) => _hasher.HashPassword(user, password);

    /// <summary>
    /// Valida las credenciales. Devuelve el usuario si son correctas, o un mensaje
    /// de error apto para mostrar.
    ///
    /// El mensaje de credenciales inválidas es DELIBERADAMENTE genérico: no revela
    /// si el usuario existe, para no permitir enumerar cuentas.
    /// </summary>
    public async Task<(AppUser? User, string? Error)> ValidateAsync(string userName, string password)
    {
        const string generic = "Usuario o contraseña incorrectos.";

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return (null, generic);

        var user = await users.GetByUserNameAsync(userName);
        if (user is null)
        {
            logger.LogWarning("Intento de acceso con usuario inexistente: {User}", userName);
            return (null, generic);
        }

        if (!user.IsActive)
        {
            logger.LogWarning("Intento de acceso de usuario desactivado: {User}", user.UserName);
            return (null, "Esta cuenta está desactivada. Contacta con el administrador.");
        }

        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
            return (null, $"Cuenta bloqueada por intentos fallidos. Inténtalo de nuevo en {mins} minuto(s).");
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedAttempts = 0;
                logger.LogWarning("Usuario {User} bloqueado tras {N} intentos fallidos.",
                    user.UserName, MaxFailedAttempts);
            }
            await users.UpdateAsync(user);
            return (null, generic);
        }

        // Éxito: si el hash usaba parámetros antiguos, se regenera de forma transparente.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = Hash(user, password);

        user.FailedAttempts = 0;
        user.LockedUntil    = null;
        user.LastLoginDate  = DateTime.UtcNow;
        await users.UpdateAsync(user);

        logger.LogInformation("Acceso correcto: {User} ({Role})", user.UserName, user.Role);
        return (user, null);
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
