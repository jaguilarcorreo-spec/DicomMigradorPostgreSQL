# DICOM Migrator

Aplicación **Blazor Server (.NET 9)** para migrar estudios DICOM entre sistemas PACS,
con descubrimiento de inventario, migración multihilo gobernada (C-MOVE), verificación
independiente y auditoría.

Construida sobre **fo-dicom 5.2.2**, **EF Core 9** y **PostgreSQL**.

## Arquitectura

Tres capas:

- `DicomMigrator.Core` — modelos, DTOs e interfaces.
- `DicomMigrator.Infrastructure` — acceso a datos (EF Core / PostgreSQL), servicios DIMSE/DICOMweb, motor de descubrimiento y migración.
- `DicomMigrator.Web` — interfaz Blazor Server y servicios alojados (scheduler, auto-resume, mantenimiento, flush de auditoría).

> Nota: los nombres de ensamblado y los espacios de nombres mantienen el prefijo
> `DicomMigrator.*` por estabilidad de compilación; el nombre de producto es
> *DICOM Migrator*.

## Requisitos

- .NET 9 SDK
- PostgreSQL (local, en red, o en contenedor)

## Base de datos

La aplicación usa **PostgreSQL exclusivamente**. La cadena de conexión se configura en
`src/DicomMigrator.Web/appsettings.json`:

```json
"ConnectionStrings": {
  "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=postgres;Password=postgres"
}
```

Para desarrollo y pruebas puedes levantar un PostgreSQL en segundos con Docker:

```bash
docker run --name dicommigrador-pg -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=dicommigrator -p 5432:5432 -d postgres:18
```

### Credenciales (no se suben al repositorio)

`appsettings.json` se versiona **sin contraseña**. La cadena real se resuelve por
prioridad (gana la última fuente que la defina):

1. `appsettings.json` — placeholder sin contraseña (en el repo).
2. `appsettings.Development.json` — tu cadena real para desarrollo local. Está en
   `.gitignore`, así que no se sube. Ejemplo:

   ```json
   {
     "ConnectionStrings": {
       "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=postgres;Password=TU_PASSWORD"
     }
   }
   ```

3. Variable de entorno — para producción, sin tocar ficheros (el `__` separa secciones):

   ```bash
   set ConnectionStrings__Default=Host=localhost;Port=5432;Database=dicommigrator;Username=postgres;Password=TU_PASSWORD
   ```

> Para generar migraciones con `dotnet ef`, el `DesignTimeDbContextFactory` usa la
> variable `DICOMMIGRATOR_DESIGN_CONNSTR` si está definida; si no, una cadena por
> defecto. No subas contraseñas reales en ningún fichero versionado.

## Ejecución

```bash
cd src/DicomMigrator.Web
dotnet run
```

La interfaz queda disponible en la URL configurada en `Kestrel` dentro de
`appsettings.json` (por defecto `http://localhost:5200`).

## Publicación (ejecutable autónomo de Windows)

```bash
dotnet publish src/DicomMigrator.Web -c Release -r win-x64 --self-contained true
```

## Mantenimiento

El mantenimiento rutinario de espacio en PostgreSQL lo realiza *autovacuum*. Para un
mantenimiento manual bajo demanda (tras borrados masivos) existe un modo offline que
ejecuta `VACUUM` + `ANALYZE` y termina sin levantar el servidor web:

```bash
dotnet run --project src/DicomMigrator.Web -- --maintenance
```

## Estado

- Migración y verificación como procesos gobernados, simétricos e independientes (Iniciar/Pausar/Reanudar/Parar).
- Clasificación de errores de conexión separada de los fallos lógicos (los transitorios no consumen reintentos).
- Auditoría con escritura por lotes y purga por retención configurable.
- Flujo de estado: `Running` → `Migrated` (migrado, pendiente de verificar) → `Completed` (migrado y verificado).
