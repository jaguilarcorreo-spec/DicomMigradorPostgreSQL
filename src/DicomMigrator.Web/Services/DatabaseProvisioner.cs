using Npgsql;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Modo CLI <c>--setup-db</c>, pensado para el instalador. Termina sin levantar el
/// servidor web y devuelve código de salida 0 si la aplicación puede conectarse.
///
/// Lee la cadena de la aplicación de la configuración normal (appsettings.json,
/// appsettings.Production.json y variables de entorno, junto al ejecutable). Si además
/// se define la variable <c>DICOMMIGRATOR_SETUP_ADMIN_CONNSTR</c> (conexión de un
/// superusuario, p. ej. postgres), antes crea el rol y la base del Modelo A si no
/// existen: <c>CREATE ROLE ... LOGIN</c> y <c>CREATE DATABASE ... OWNER</c>. Nunca
/// modifica un rol o una base que ya existan.
///
/// La contraseña de superusuario se pasa por variable de entorno (heredada del proceso
/// del instalador) para que no aparezca en la línea de comandos.
/// </summary>
public static class DatabaseProvisioner
{
    public const string AdminConnStrVariable = "DICOMMIGRATOR_SETUP_ADMIN_CONNSTR";

    public static async Task<int> RunAsync()
    {
        // El instalador captura la salida; en UTF-8 los acentos llegan intactos (con la
        // página OEM por defecto se ven como '?').
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Production.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var appConnStr = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(appConnStr))
        {
            Console.WriteLine("ERROR: no hay cadena de conexión (ConnectionStrings:Default).");
            return 2;
        }

        NpgsqlConnectionStringBuilder app;
        try { app = new NpgsqlConnectionStringBuilder(appConnStr); }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: cadena de conexión no válida: {ex.Message}");
            return 2;
        }

        var roleExisted = false;
        var adminConnStr = Environment.GetEnvironmentVariable(AdminConnStrVariable);
        if (!string.IsNullOrWhiteSpace(adminConnStr))
        {
            try
            {
                roleExisted = await ProvisionAsync(adminConnStr, app);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR al crear rol/base como superusuario: {ex.Message}");
                return 3;
            }
        }

        try
        {
            await using var conn = new NpgsqlConnection(app.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT version()", conn);
            var version = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"OK: conexión como '{app.Username}' a '{app.Database}' en {app.Host}:{app.Port}.");
            Console.WriteLine($"    {version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR al conectar como '{app.Username}' a '{app.Database}' en {app.Host}:{app.Port}: {ex.Message}");
            if (roleExisted && ex is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword })
                Console.WriteLine($"El rol '{app.Username}' ya existía y su contraseña no se modifica: indica la " +
                                  $"contraseña actual, o cámbiala con ALTER ROLE {Ident(app.Username!)} WITH PASSWORD '...'.");
            return 4;
        }
    }

    /// <returns>true si el rol de aplicación ya existía (su contraseña no se ha tocado).</returns>
    private static async Task<bool> ProvisionAsync(string adminConnStr, NpgsqlConnectionStringBuilder app)
    {
        var roleExisted = false;
        if (string.IsNullOrWhiteSpace(app.Username) || string.IsNullOrWhiteSpace(app.Database))
            throw new InvalidOperationException("La cadena de la aplicación debe indicar Username y Database.");

        // Siempre contra la base de mantenimiento: CREATE DATABASE no puede ejecutarse
        // conectado a la base que se crea, y ésta aún no existe.
        var admin = new NpgsqlConnectionStringBuilder(adminConnStr) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @n", conn))
        {
            exists.Parameters.AddWithValue("n", app.Username);
            if (await exists.ExecuteScalarAsync() is null)
            {
                // CREATE ROLE no admite parámetros: se citan identificador y literal.
                var sql = $"CREATE ROLE {Ident(app.Username)} WITH LOGIN PASSWORD {Literal(app.Password ?? "")}";
                await using var create = new NpgsqlCommand(sql, conn);
                await create.ExecuteNonQueryAsync();
                Console.WriteLine($"Rol '{app.Username}' creado.");
            }
            else
            {
                roleExisted = true;
                Console.WriteLine($"Rol '{app.Username}' ya existía (no se modifica).");
            }
        }

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn))
        {
            exists.Parameters.AddWithValue("n", app.Database);
            if (await exists.ExecuteScalarAsync() is null)
            {
                var sql = $"CREATE DATABASE {Ident(app.Database)} OWNER {Ident(app.Username)}";
                await using var create = new NpgsqlCommand(sql, conn);
                await create.ExecuteNonQueryAsync();
                Console.WriteLine($"Base '{app.Database}' creada con propietario '{app.Username}'.");
            }
            else
            {
                Console.WriteLine($"Base '{app.Database}' ya existía (no se modifica).");
            }
        }

        return roleExisted;
    }

    private static string Ident(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // Válido con standard_conforming_strings=on (valor por defecto desde PostgreSQL 9.1).
    private static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
}
