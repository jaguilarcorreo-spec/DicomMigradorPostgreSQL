namespace DicomMigrator.Web.Services;

/// <summary>
/// Limitación de intentos de acceso (SEC-6). Sustituye al bloqueo de cuenta tras 5 fallos,
/// que permitía a cualquiera dejar fuera al administrador tecleando 5 contraseñas malas
/// cada 15 minutos, y que además revelaba qué usuarios existen (solo las cuentas reales
/// llegaban a bloquearse).
///
/// Tres contadores, todos en ventanas de <c>Auth:LoginLockoutMinutes</c>:
///   · Usuario + IP (<c>Auth:LoginMaxFailuresPerUser</c>, 5): frena el ataque a una cuenta
///     desde un equipo sin afectar al dueño de la cuenta, que entra desde el suyo.
///   · IP (<c>Auth:LoginMaxFailuresPerIp</c>, 20; 0 = sin límite): frena probar muchos
///     usuarios desde un equipo (enumeración, una contraseña contra todas las cuentas).
///   · Usuario, desde cualquier IP (<c>Auth:LoginMaxFailuresPerAccount</c>, 20): tope frente
///     a un ataque repartido entre varios equipos. Para bloquear así al administrador hacen
///     falta al menos 4 equipos atacando a la vez (5 fallos por equipo y ventana).
/// Se cuentan igual los nombres que existen y los que no, así que la respuesta "demasiados
/// intentos" no indica si la cuenta existe.
///
/// Cada intento cuenta como fallo AL EMPEZAR (y se descuenta si acierta). Así, peticiones
/// en paralelo no pueden colarse entre la comprobación y el registro del fallo.
///
/// Vive en memoria (singleton): una sola instancia de la aplicación. Un reinicio vacía los
/// contadores; el bloqueo de cuenta se guarda además en la BD (AppUser.LockedUntil) para
/// que lo vea y lo pueda levantar el administrador en <c>Usuarios</c>.
/// </summary>
public sealed class LoginThrottle
{
    private sealed class Window
    {
        public int      Count;
        public DateTime Start;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private DateTime _nextPurge = DateTime.MinValue;

    public int      MaxPerUserAndIp  { get; }
    public int      MaxPerIp         { get; }
    public int      MaxPerAccount    { get; }
    public TimeSpan Period           { get; }

    public LoginThrottle(IConfiguration configuration)
    {
        MaxPerUserAndIp = Math.Max(1, configuration.GetValue("Auth:LoginMaxFailuresPerUser", 5));
        MaxPerIp        = Math.Max(0, configuration.GetValue("Auth:LoginMaxFailuresPerIp", 20));
        MaxPerAccount   = Math.Max(1, configuration.GetValue("Auth:LoginMaxFailuresPerAccount", 20));
        Period          = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Auth:LoginLockoutMinutes", 15), 1, 1440));
    }

    private static string UserIpKey(string user, string ip) => "u|" + user + "|" + ip;
    private static string IpKey(string ip)                  => "i|" + ip;
    private static string AccountKey(string user)           => "a|" + user;

    /// <summary>
    /// Registra el comienzo de un intento. Devuelve false, con la espera restante, si
    /// cualquiera de los tres límites está agotado; en ese caso no hay que comprobar la
    /// contraseña. Si devuelve true, el intento ya cuenta como fallo hasta que se llame a
    /// <see cref="Succeeded"/>.
    /// </summary>
    public bool TryBegin(string user, string ip, out TimeSpan retryAfter)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            PurgeIfDue(now);

            var keys = new List<(string Key, int Max)> { (UserIpKey(user, ip), MaxPerUserAndIp), (AccountKey(user), MaxPerAccount) };
            if (MaxPerIp > 0) keys.Add((IpKey(ip), MaxPerIp));

            retryAfter = TimeSpan.Zero;
            foreach (var (key, max) in keys)
            {
                var w = Current(key, now);
                if (w is not null && w.Count >= max)
                {
                    var wait = w.Start + Period - now;
                    if (wait > retryAfter) retryAfter = wait;
                }
            }
            if (retryAfter > TimeSpan.Zero) return false;

            foreach (var (key, _) in keys)
            {
                var w = Current(key, now);
                if (w is null) _windows[key] = w = new Window { Start = now };
                w.Count++;
            }
            return true;
        }
    }

    /// <summary>True si el usuario ha agotado los fallos de cuenta (desde cualquier IP) en
    /// la ventana actual. Se consulta tras un fallo para guardar el bloqueo en la BD.</summary>
    public bool AccountExhausted(string user, out DateTime until)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            var w = Current(AccountKey(user), now);
            until = w is null ? now : w.Start + Period;
            return w is not null && w.Count >= MaxPerAccount;
        }
    }

    /// <summary>Acceso correcto: se borran los fallos de ese usuario (en esa IP y en la
    /// cuenta) y se descuenta el intento del contador de la IP, para que muchos accesos
    /// correctos desde un equipo compartido (p. ej. Citrix) no la bloqueen.</summary>
    public void Succeeded(string user, string ip)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            _windows.Remove(UserIpKey(user, ip));
            _windows.Remove(AccountKey(user));
            if (Current(IpKey(ip), now) is { Count: > 0 } w) w.Count--;
        }
    }

    /// <summary>True si el usuario tiene algún límite agotado (desde alguna IP o en la
    /// cuenta). Lo usa la pantalla de Usuarios para mostrarlo como bloqueado.</summary>
    public bool IsBlocked(string user)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var (key, w) in _windows)
            {
                if (now - w.Start >= Period) continue;
                if (key == AccountKey(user) && w.Count >= MaxPerAccount) return true;
                if (key.StartsWith("u|" + user + "|", StringComparison.Ordinal) && w.Count >= MaxPerUserAndIp) return true;
            }
            return false;
        }
    }

    /// <summary>Desbloqueo desde Usuarios (o al resetear la contraseña): borra los fallos
    /// de ese usuario en todas las IP y en la cuenta.</summary>
    public void Reset(string user)
    {
        lock (_gate)
        {
            var prefix = "u|" + user + "|";
            foreach (var key in _windows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _windows.Remove(key);
            _windows.Remove(AccountKey(user));
        }
    }

    /// <summary>Ventana vigente de la clave, o null si no hay o ya caducó.</summary>
    private Window? Current(string key, DateTime now)
    {
        if (!_windows.TryGetValue(key, out var w)) return null;
        if (now - w.Start < Period) return w;
        _windows.Remove(key);
        return null;
    }

    /// <summary>Quita las ventanas caducadas cada minuto, para que los nombres e IP de
    /// intentos antiguos no se acumulen en memoria.</summary>
    private void PurgeIfDue(DateTime now)
    {
        if (now < _nextPurge) return;
        _nextPurge = now.AddMinutes(1);
        foreach (var key in _windows.Where(kv => now - kv.Value.Start >= Period).Select(kv => kv.Key).ToList())
            _windows.Remove(key);
    }
}
