using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using DicomMigrator.Infrastructure.Repositories;
using DicomMigrator.Infrastructure.Services.DicomWeb;
using DicomMigrator.Infrastructure.Services.Dimse;
using DicomMigrator.Infrastructure.Services.Migration;
using DicomMigrator.Web.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// ── Serilog bootstrap logger (idéntico al Tester) ───────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Iniciando DicomMigrator...");

    var builder = WebApplication.CreateBuilder(args);

    // Permite ejecutar la app como Servicio de Windows (modo desatendido), integrándose
    // con el Service Control Manager (start/stop/reinicio). No tiene efecto cuando se
    // arranca a mano con 'dotnet run' o como consola, así que no afecta al desarrollo.
    // Para registrarla como servicio, ver DEPLOYMENT.md.
    builder.Host.UseWindowsService(o => o.ServiceName = "DicomMigrator");

    // ── Serilog (mismo patrón que el Tester) ─────────────────────────────────
    // Nivel inicial tomado de configuración (Serilog:MinimumLevel:Default); el switch
    // permite además cambiarlo en caliente desde la UI (Logs y auditoría).
    LogLevelService.InitializeFromConfig(builder.Configuration["Serilog:MinimumLevel:Default"]);

    var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "dicommigrator-.log");

    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.MinimumLevel.ControlledBy(LogLevelService.Switch)
           .MinimumLevel.Override("Microsoft",            LogEventLevel.Warning)
           .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
           .MinimumLevel.Override("System",               LogEventLevel.Warning)
           .WriteTo.File(
               logPath,
               rollingInterval: Serilog.RollingInterval.Day,
               retainedFileCountLimit: 30,
               fileSizeLimitBytes: 52_428_800,
               rollOnFileSizeLimit: true,
               outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
           .WriteTo.Console()
           .ReadFrom.Services(services)
           .Enrich.FromLogContext());

    // ── Blazor ───────────────────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // ── EF Core / PostgreSQL ─────────────────────────────────────────────────
    // PostgreSQL es el único motor soportado. El servidor arbitra la concurrencia
    // (MVCC), por lo que no hay PRAGMAs ni interceptor de conexión, y las claves
    // foráneas están siempre activas.
    //
    // La cadena de conexión se resuelve en este orden de prioridad (gana el último):
    //   1) appsettings.json            → SIN contraseña (apto para el repo público)
    //   2) appsettings.Development.json → cadena real para desarrollo local (git-ignored)
    //   3) Variable de entorno          → para producción, sin tocar ficheros:
    //        ConnectionStrings__Default=Host=...;Username=...;Password=...
    //      (el doble guion bajo "__" es el separador de secciones en .NET)
    var connStr = builder.Configuration.GetConnectionString("Default")
        ?? "Host=localhost;Port=5432;Database=dicommigrator;Username=postgres;Password=postgres";

    // Aviso temprano y claro si la contraseña quedó vacía (p. ej. se desplegó solo con
    // el appsettings.json del repo sin definir el secreto). Evita un 28P01 críptico.
    if (System.Text.RegularExpressions.Regex.IsMatch(connStr, @"Password=\s*(;|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        Console.WriteLine("[AVISO] La cadena de conexión no tiene contraseña. Defínela en " +
            "appsettings.Development.json (local) o en la variable de entorno " +
            "ConnectionStrings__Default (producción).");
    }

    // IDbContextFactory en lifetime Singleton (el valor por defecto y el recomendado
    // para Blazor Server): los contextos que crea NO dependen del scope del circuito,
    // así que una operación async que termina justo cuando el circuito se desconecta
    // ya no provoca ObjectDisposedException sobre el IServiceProvider del scope.
    // Cada repositorio sigue creando y disponiendo su propio contexto por operación.
    builder.Services.AddDbContextFactory<AppDbContext>(opt => opt
        .UseNpgsql(connStr, o => o.SetPostgresVersion(18, 0))
        .ConfigureWarnings(w => w.Ignore(
            Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.FirstWithoutOrderByAndFilterWarning)));

    // ── Repositorios ─────────────────────────────────────────────────────────
    builder.Services.AddScoped<DicomMigrator.Infrastructure.Data.DatabaseMaintenance>();
    builder.Services.AddScoped<INodeRepository,        NodeRepository>();
    builder.Services.AddScoped<IMigrationRepository,   MigrationRepository>();
    builder.Services.AddScoped<IStudyRepository,       StudyRepository>();
    builder.Services.AddScoped<IAuditLogRepository,    AuditLogRepository>();
    builder.Services.AddSingleton<DicomMigrator.Infrastructure.Data.AuditLogBuffer>();
    builder.Services.AddScoped<ILocalConfigRepository, LocalConfigRepository>();

    // Discovery Engine (RF-020)
    builder.Services.AddScoped<IDiscoveryJobRepository,      DiscoveryJobRepository>();
    builder.Services.AddScoped<IDiscoveredStudyRepository,   DiscoveredStudyRepository>();

    // ── Servicios DICOM (mismas implementaciones que el Tester, adaptadas) ───
    builder.Services.AddScoped<IDimseService,    DimseService>();
    builder.Services.AddScoped<IDicomWebService, DicomWebService>();

    // ── HttpClient con socket pooling para QIDO/WADO ────────────────────────
    // IHttpClientFactory reusa HttpMessageHandler internamente; evita agotar
    // puertos efímeros con descubrimientos QIDO de miles de particiones.
    builder.Services.AddHttpClient("dicomweb-tls");
    builder.Services.AddHttpClient("dicomweb-relaxed")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    // ── Servicios de migración (nuevos) ──────────────────────────────────────
    builder.Services.AddScoped<IDiscoveryService,    DiscoveryService>();
    builder.Services.AddScoped<IVerificationService, VerificationService>();
    builder.Services.AddScoped<IConnectionHealthService, DicomMigrator.Infrastructure.Services.Migration.ConnectionHealthService>();
    builder.Services.AddSingleton<IMigrationWorker,  MigrationWorker>();
    builder.Services.AddSingleton<IWindowScheduler,  WindowScheduler>();
    builder.Services.AddSingleton<IDiscoveryEngine,  DiscoveryEngine>();

    // ── Servicios web (mismos que el Tester) ─────────────────────────────────
    builder.Services.AddSingleton<LocalConfigState>();
    builder.Services.AddSingleton<LogLevelService>();

    // ── Window Scheduler como hosted service ─────────────────────────────────
    builder.Services.AddHostedService<WindowSchedulerHostedService>();

    // ── Mantenimiento periódico de BD (purga de AuditLogs) ───────────────────
    builder.Services.AddHostedService<DatabaseMaintenanceHostedService>();

    // ── Auto-reanudación tras auto-pausa por errores de conexión ─────────────
    builder.Services.AddHostedService<AutoResumeHostedService>();
    builder.Services.AddHostedService<DicomMigrator.Web.Services.AuditLogFlushService>();

    var app = builder.Build();

    // ── Base de datos ─────────────────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var logger  = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            await using var db = await factory.CreateDbContextAsync();

            // PostgreSQL: aplicar las migraciones EF pendientes. Migrate() crea el
            // esquema en una base vacía y lo evoluciona en bases existentes, todo a
            // partir de las migraciones versionadas en Infrastructure/Migrations.
            // Los índices de rendimiento están en el modelo, así que las migraciones
            // los crean (con el dueño del esquema, sin problemas de permisos).
            await db.Database.MigrateAsync();
            logger.LogInformation("Base de datos PostgreSQL migrada al último esquema.");

            // ── Reanudación de migraciones/verificaciones huérfanas ──────────────
            // El control de los workers vive en memoria del proceso. Si el proceso se
            // detuvo (parada del servicio, caída) con una migración en curso, ésta
            // queda con Status="Running" en la BD pero sin workers reales. En un
            // despliegue de INSTANCIA ÚNICA, cualquier cosa "Running" recién arrancada
            // es necesariamente huérfana (no hay otra instancia ejecutándola), así que
            // la relanzamos. El lock atómico de adquisición evita reprocesar estudios
            // ya migrados (los workers solo toman los que siguen "Pending").
            try
            {
                var migRepo = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
                var studyRepo = scope.ServiceProvider.GetRequiredService<IStudyRepository>();
                var worker  = scope.ServiceProvider.GetRequiredService<IMigrationWorker>();
                var verSvc  = scope.ServiceProvider.GetRequiredService<IVerificationService>();

                var all = await migRepo.GetAllAsync();
                foreach (var m in all)
                {
                    if (m.Status == "Running")
                    {
                        // Liberar locks huérfanos (estudios 'Queued' o con lock de
                        // verificación) que quedaron de la ejecución anterior, para que
                        // los workers los vuelvan a tomar de inmediato sin esperar el
                        // timeout de 10 min.
                        await studyRepo.ReleaseOrphanLocksAsync(m.Id);
                        logger.LogInformation("Reanudando migración huérfana {Id} ('{Name}') tras reinicio.", m.Id, m.Name);
                        await worker.StartAsync(m.Id);
                    }
                    if (m.VerificationStatus == "Running")
                    {
                        if (m.Status != "Running")  // evitar liberar dos veces si ya se hizo arriba
                            await studyRepo.ReleaseOrphanLocksAsync(m.Id);
                        logger.LogInformation("Reanudando verificación huérfana {Id} ('{Name}') tras reinicio.", m.Id, m.Name);
                        await verSvc.StartVerificationAsync(m.Id);
                    }
                }
            }
            catch (Exception exResume)
            {
                logger.LogWarning(exResume, "No se pudieron reanudar migraciones en curso al arrancar (no crítico).");
            }
        }
        catch (Exception ex)
        {
            var logger2 = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger2.LogError(ex, "Error al inicializar la base de datos.");
            throw; // Detener el arranque si la BD no está disponible
        }
    }

    // ── Modo de mantenimiento offline ────────────────────────────────────────
    // Termina SIN levantar el servidor web. En PostgreSQL el mantenimiento de
    // espacio lo hace autovacuum; este modo ejecuta un VACUUM + ANALYZE explícito
    // bajo demanda, útil tras borrados masivos.
    //   DicomMigrator.exe --maintenance       → VACUUM + ANALYZE
    //   DicomMigrator.exe --maintenance-full  → igual (la "Fase D" de FKs era
    //                                            específica de SQLite y ya no aplica)
    var maintenanceMode = args.Any(a => string.Equals(a, "--maintenance", StringComparison.OrdinalIgnoreCase));
    var maintenanceFull = args.Any(a => string.Equals(a, "--maintenance-full", StringComparison.OrdinalIgnoreCase));
    if (maintenanceMode || maintenanceFull)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var maint  = scope.ServiceProvider
            .GetRequiredService<DicomMigrator.Infrastructure.Data.DatabaseMaintenance>();

        logger.LogInformation("=== MODO MANTENIMIENTO (PostgreSQL) ===");
        try
        {
            // VACUUM (recupera espacio) — en Postgres no requiere fichero ni PRAGMAs.
            logger.LogInformation("Ejecutando VACUUM...");
            await maint.RunVacuumAsync(string.Empty);

            // ANALYZE — refresca estadísticas del planificador.
            logger.LogInformation("Ejecutando ANALYZE...");
            await maint.OptimizeAsync();

            logger.LogInformation("✓ Mantenimiento finalizado correctamente.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error durante el mantenimiento.");
        }

        Log.CloseAndFlush();
        return; // No se levanta el servidor web
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    // Solo redirigir a HTTPS si hay un endpoint HTTPS configurado. En un despliegue
    // HTTP (p. ej. tras un proxy inverso que termina TLS, o en desarrollo local en el
    // puerto 5200), activar la redirección sin puerto HTTPS provoca el aviso
    // "Failed to determine the https port for redirect" y no redirige a nada útil.
    var hasHttps =
        !string.IsNullOrEmpty(builder.Configuration["Kestrel:Endpoints:Https:Url"])
        || !string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTPS_PORT"])
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT"));
    if (hasHttps)
        app.UseHttpsRedirection();

    app.UseStaticFiles();
    app.UseSerilogRequestLogging();
    app.UseAntiforgery();

    app.MapRazorComponents<DicomMigrator.Web.Components.App>()
        .AddInteractiveServerRenderMode();

    // ── Streaming CSV export endpoint ─────────────────────────────────────────
    // Streams the discovered-study inventory directly to the HTTP response
    // without holding the full result set in memory or routing through SignalR.
    // Replaces the previous Blazor-side approach (StringBuilder + base64 + JS download)
    // which OOM'd / broke SignalR with large inventories.
    app.MapGet("/migrations/{migrationId:int}/export.csv",
        async (int migrationId,
               IMigrationRepository migrationRepo,
               IStudyRepository studyRepo,
               HttpContext ctx,
               string? status,
               string? modality,
               string? uid,
               string? patientId,
               string? accession,
               CancellationToken ct) =>
    {
        var migration = await migrationRepo.GetByIdAsync(migrationId);
        if (migration is null) return Results.NotFound();

        var safeName = migration.Name;
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        var filename = $"migration_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

        var filter = new StudyFilter
        {
            Status           = status,
            Modality         = modality,
            StudyInstanceUid = uid,
            PatientId        = patientId,
            AccessionNumber  = accession,
        };

        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        await ctx.Response.Body.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, ct);

        await using var writer = new StreamWriter(ctx.Response.Body,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        await writer.WriteLineAsync(
            "StudyInstanceUID,PatientID,AccessionNumber,StudyDate,Modality,Status," +
            "SrcSeries,SrcInstances,TgtSeries,TgtInstances,Retries,LastError," +
            "DiscoveryDate,MigrationDate,VerificationDate");

        int flushEvery = 0;
        await foreach (var s in studyRepo.StreamForExportAsync(migrationId, filter, ct))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                CsvField(s.StudyInstanceUid), CsvField(s.PatientId), CsvField(s.AccessionNumber),
                CsvField(s.StudyDate), CsvField(s.ModalitiesInStudy), CsvField(s.MigrationStatus),
                s.SourceSeriesCount?.ToString() ?? "", s.SourceInstanceCount?.ToString() ?? "",
                s.TargetSeriesCount?.ToString() ?? "", s.TargetInstanceCount?.ToString() ?? "",
                s.RetryCount.ToString(), CsvField(s.LastError),
                s.DiscoveryDate.ToString("yyyy-MM-dd HH:mm:ss"),
                s.MigrationDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                s.VerificationDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            }));
            if (++flushEvery >= 500) { await writer.FlushAsync(ct); flushEvery = 0; }
        }
        await writer.FlushAsync(ct);
        return Results.Empty;

        static string CsvField(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? $"\"{v.Replace("\"", "\"\"")}\""
                : v;
        }
    });

    app.MapGet("/discovery/{jobId:int}/export.csv",
        async (int jobId,
               IDiscoveryJobRepository jobRepo,
               IDiscoveredStudyRepository studyRepo,
               HttpContext ctx,
               CancellationToken ct) =>
    {
        var job = await jobRepo.GetByIdAsync(jobId);
        if (job is null) return Results.NotFound();

        // Sanitize filename — strip characters illegal on Windows/macOS
        var safeName = job.Name;
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        var filename = $"discovery_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";

        // UTF-8 BOM so Excel opens it with the right encoding
        await ctx.Response.Body.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, ct);

        await using var writer = new StreamWriter(ctx.Response.Body,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        await writer.WriteLineAsync(
            "StudyInstanceUID,PatientID,PatientName,AccessionNumber,StudyDate,StudyTime," +
            "Modalities,Series,Instances,StudyDescription,InstitutionName,DiscoveryDate");

        int flushEvery = 0;
        await foreach (var s in studyRepo.StreamForExportAsync(
            new DiscoveredStudyFilter { DiscoveryJobId = jobId }, ct))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                CsvField(s.StudyInstanceUid), CsvField(s.PatientId), CsvField(s.PatientName),
                CsvField(s.AccessionNumber), CsvField(s.StudyDate), CsvField(s.StudyTime),
                CsvField(s.ModalitiesInStudy),
                s.NumberOfStudyRelatedSeries?.ToString() ?? "",
                s.NumberOfStudyRelatedInstances?.ToString() ?? "",
                CsvField(s.StudyDescription), CsvField(s.InstitutionName),
                s.DiscoveryDate.ToString("yyyy-MM-dd HH:mm:ss"),
            }));
            // Flush every 500 rows so the client starts receiving immediately
            if (++flushEvery >= 500) { await writer.FlushAsync(ct); flushEvery = 0; }
        }
        await writer.FlushAsync(ct);
        return Results.Empty;

        static string CsvField(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? $"\"{v.Replace("\"", "\"\"")}\""
                : v;
        }
    });

    // Al cerrar la aplicación: refrescar estadísticas del planificador (Fase B).
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var maint = scope.ServiceProvider
                .GetRequiredService<DicomMigrator.Infrastructure.Data.DatabaseMaintenance>();
            maint.OptimizeAsync().GetAwaiter().GetResult();
        }
        catch { /* el cierre nunca debe fallar por mantenimiento */ }
    });

    app.Run();
}
catch (Exception ex) { Log.Fatal(ex, "DicomMigrator terminó inesperadamente"); }
finally { Log.CloseAndFlush(); }
