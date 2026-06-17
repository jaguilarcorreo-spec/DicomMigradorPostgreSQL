using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// Permite a las herramientas de EF Core (dotnet ef migrations add / database update)
/// construir el AppDbContext en tiempo de diseño, sin arrancar la aplicación web.
///
/// La herramienta busca automáticamente una implementación de
/// IDesignTimeDbContextFactory en el ensamblado. La cadena de conexión solo se usa
/// para generar/aplicar migraciones desde la línea de comandos; en ejecución real la
/// conexión viene de appsettings.json (configurada en Program.cs).
///
/// Para apuntar a otra base en tiempo de diseño, define la variable de entorno
/// DICOMMIGRATOR_DESIGN_CONNSTR antes de ejecutar dotnet ef.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("DICOMMIGRATOR_DESIGN_CONNSTR")
            ?? "Host=localhost;Port=5432;Database=dicommigrator;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.SetPostgresVersion(18, 0))
            .Options;

        return new AppDbContext(options);
    }
}
