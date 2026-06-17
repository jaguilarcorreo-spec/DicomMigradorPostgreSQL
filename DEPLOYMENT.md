# Despliegue de DICOM Migrador (PostgreSQL)

Procedimiento reproducible para desplegar la aplicación contra una base PostgreSQL
limpia, siguiendo el **Modelo A**: el usuario de aplicación es dueño del esquema y
la propia app aplica las migraciones al arrancar.

## Requisitos

- PostgreSQL 16 o superior (probado en 18).
- .NET 9 SDK (para compilar/publicar) y, si se generan migraciones, la herramienta
  `dotnet-ef` (`dotnet tool install --global dotnet-ef`).

## Modelo de roles

- **`postgres`** (superusuario): solo tareas administrativas iniciales (crear el rol
  de aplicación y la base). No lo usa la app.
- **`dicom_app_migrator`** (usuario de aplicación): dueño del esquema. Lo usa la app
  en ejecución y también para aplicar las migraciones. Confinado a su base; no puede
  tocar otras bases ni el servidor.

Como la base se crea con `OWNER dicom_app_migrator` y las migraciones las aplica ese
mismo usuario, **todas las tablas, índices y secuencias nacen siendo suyas**. No hace
falta reasignar propietarios (`ALTER TABLE ... OWNER TO`) en ningún momento.

## 1. Crear rol y base (una sola vez, como `postgres`)

Conéctate como `postgres` a la base de mantenimiento `postgres` (en pgAdmin, abre el
Query Tool sobre la base `postgres`, **no** sobre la base de la aplicación) y ejecuta:

```sql
-- Rol de aplicación con login y contraseña
CREATE ROLE dicom_app_migrator WITH LOGIN PASSWORD 'CONTRASEÑA_FUERTE';

-- Base propiedad del usuario de aplicación (clave del Modelo A)
CREATE DATABASE dicommigrator OWNER dicom_app_migrator;
```

Si el rol ya existe y solo necesitas (re)establecer la contraseña:

```sql
ALTER ROLE dicom_app_migrator WITH LOGIN PASSWORD 'CONTRASEÑA_FUERTE';
```

## 2. Configurar la conexión de la aplicación

La cadena de conexión NO se versiona con contraseña. Orden de prioridad (gana la última):

1. `appsettings.json` — placeholder sin contraseña (se sube al repositorio).
2. `appsettings.Development.json` — cadena real para desarrollo local (en `.gitignore`).
3. Variable de entorno `ConnectionStrings__Default` — para producción.

Para desarrollo local, crea `src/DicomMigrator.Web/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
  }
}
```

Para producción, en lugar del fichero anterior, define la variable de entorno
(el doble guion bajo `__` separa secciones):

```bash
set ConnectionStrings__Default=Host=SERVIDOR;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE
```

## 3. Aplicar el esquema

Hay dos formas equivalentes; elige una.

### Opción 3a — La app migra al arrancar (recomendada en Modelo A)

No hay que hacer nada especial: al iniciar, la app ejecuta `Migrate()` y crea/actualiza
el esquema usando la conexión configurada (usuario `dicom_app_migrator`). Simplemente
arranca la aplicación (paso 4).

### Opción 3b — Aplicar migraciones explícitamente antes de arrancar

Útil en despliegues controlados. La herramienta `dotnet ef` usa la variable
`DICOMMIGRATOR_DESIGN_CONNSTR`; **defínela con el usuario de aplicación** para que las
tablas nazcan con el propietario correcto:

```bash
set DICOMMIGRATOR_DESIGN_CONNSTR=Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE

dotnet ef database update --project src/DicomMigrator.Infrastructure --startup-project src/DicomMigrator.Web
```

> Importante: aplica las migraciones como `dicom_app_migrator`, no como `postgres`.
> Si las aplica `postgres`, las tablas quedan a su nombre y la app (que corre como
> `dicom_app_migrator`) no podría crear índices ni evolucionar el esquema
> (error `42501: debe ser dueño de la tabla`).

## 4. Ejecutar la aplicación

```bash
dotnet run --project src/DicomMigrator.Web
```

O, publicada como ejecutable autónomo de Windows:

```bash
dotnet publish src/DicomMigrator.Web -c Release -r win-x64 --self-contained true
```

La interfaz queda en la URL configurada en `Kestrel` dentro de `appsettings.json`
(por defecto `http://localhost:5200`).

## 5. Configuración inicial en la aplicación

Con la base recién creada (vacía), entra en la interfaz web y configura el entorno.
Estos ajustes se guardan en PostgreSQL:

- **SCU local**: AET y puerto de recepción del Storage SCP local (necesario para
  recibir las instancias del C-MOVE).
- **Nodos DICOM**: dar de alta el PACS de origen y el de destino (AET, host, puerto),
  y comprobarlos con un C-ECHO.
- **Ventanas de ejecución**: si la migración debe limitarse a ciertas franjas horarias.

A partir de ahí ya se pueden lanzar descubrimientos (Discovery) y migraciones.

## 6. Generar nuevas migraciones (al evolucionar el modelo)

Cuando cambie el modelo de datos, genera una migración (con la variable de diseño
apuntando a `dicom_app_migrator`, igual que en 3b):

```bash
dotnet ef migrations add NombreDescriptivo --project src/DicomMigrator.Infrastructure --startup-project src/DicomMigrator.Web
```

La migración se aplica luego con la opción 3a (al arrancar) o 3b (explícita).

## Alcance: una sola instancia

La versión actual está pensada para ejecutarse como **una única instancia**. Los
servicios en segundo plano (planificador de ventanas, auto-reanudación, mantenimiento,
flush de auditoría) y el control de migración asumen un solo proceso. Ejecutar varias
instancias contra la misma base **no está soportado todavía**: requeriría coordinar
esos servicios entre procesos (elección de líder), mover el control de migración a la
base en lugar de a memoria, y resolver el SCP de recepción y las sesiones de Blazor
por instancia. Es trabajo de una fase de escalado horizontal pendiente.

## Mantenimiento

PostgreSQL recupera espacio con *autovacuum*. Para un mantenimiento manual bajo demanda
(tras borrados masivos) existe un modo offline que ejecuta `VACUUM` + `ANALYZE` y termina
sin levantar el servidor web:

```bash
dotnet run --project src/DicomMigrator.Web -- --maintenance
```

## Resolución de problemas

- **`28P01: la autentificación password falló`** — usuario o contraseña incorrectos en
  la cadena de conexión. Verifica con `ALTER ROLE ... WITH PASSWORD` y revisa qué fichero
  de configuración (o variable de entorno) está usando la app.
- **`42501: debe ser dueño de la tabla`** — las migraciones se aplicaron con un usuario
  distinto al que ejecuta la app. Solución de raíz: recrear la base con
  `OWNER dicom_app_migrator` y aplicar las migraciones como ese usuario.
- **`55006: no se puede eliminar la base de datos activa`** — hay conexiones abiertas a
  la base. Ejecuta el `DROP` desde la base `postgres` (no desde la base a borrar) y, si
  hace falta, cierra las sesiones con
  `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'dicommigrator' AND pid <> pg_backend_pid();`
