# Despliegue de DICOM Migrator (PostgreSQL)

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
3. `appsettings.Production.json` — cadena real para el despliegue (en `.gitignore`).
4. Variable de entorno `ConnectionStrings__Default` — máxima prioridad.

Para desarrollo local, crea `src/DicomMigrator.Web/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
  }
}
```

Para producción tienes dos alternativas equivalentes; elige una.

**Opción A — fichero `appsettings.Production.json` (recomendada para servicio).** Crea este
fichero DIRECTAMENTE en la carpeta de despliegue (p. ej. `C:\DicomMigrador`), no en el
proyecto. El servicio corre en entorno Production, así que lo lee automáticamente y su
cadena gana sobre el placeholder. Está excluido de la publicación, de modo que NO se
sobrescribe al republicar:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
  }
}
```

**Opción B — variable de entorno de máquina** (el doble guion bajo `__` separa secciones):

```bash
setx /M ConnectionStrings__Default "Host=SERVIDOR;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
```

> Si defines ambas, la variable de entorno gana sobre el fichero. Usa solo una para
> evitar confusión sobre qué cadena está activa.

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

## Ejecutar como Servicio de Windows (modo desatendido)

Para que la aplicación arranque sola con el sistema, sobreviva a reinicios y no dependa
de una consola abierta, se registra como Servicio de Windows. La app ya incluye el
soporte (`UseWindowsService`); solo hay que publicarla y registrar el servicio.

**Antes de empezar:**

- Detén cualquier instancia de la app que esté corriendo a mano (Ctrl+C), porque el
  servicio usará el mismo puerto (5200 por defecto).
- Los pasos 2, 3 y 4 requieren una consola **abierta como administrador**.
- PostgreSQL debe estar accesible con el usuario `dicom_app_migrator` (ver pasos 1 y 2
  de la sección de despliegue).

### 1. Publicar el ejecutable autónomo

```bash
dotnet publish src/DicomMigrator.Web -c Release -r win-x64 --self-contained true -o C:\DicomMigrador
```

Esto deja el `.exe` y sus dependencias en `C:\DicomMigrador` (elige la ruta que prefieras).

### 2. Configurar la conexión para el servicio

Un servicio no hereda las variables de entorno de tu sesión. Define la cadena de
conexión de forma persistente y accesible para el servicio. Opción recomendada: variable
de entorno **de máquina** (no de usuario), como administrador:

```bash
setx /M ConnectionStrings__Default "Host=SERVIDOR;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
```

(El `appsettings.Development.json` NO se usa aquí: el servicio corre en entorno
Production, así que la cadena debe venir de la variable de entorno de máquina o de un
`appsettings.json` desplegado junto al .exe.)

### 3. Crear el servicio

Como administrador (los espacios tras `binPath=` y `start=` son obligatorios en `sc`):

```bash
sc create DicomMigrator binPath= "C:\DicomMigrador\DicomMigrator.Web.exe" start= auto DisplayName= "DICOM Migrator"
sc description DicomMigrator "Migración de estudios DICOM entre sistemas PACS."
```

### 4. Arrancar y verificar

```bash
sc start DicomMigrator
sc query DicomMigrator
```

Debe aparecer `STATE: 4 RUNNING`. La interfaz queda en la URL de Kestrel
(por defecto `http://localhost:5200`). Los logs van a `C:\DicomMigrador\logs\`.

### Gestión del servicio

```bash
sc stop DicomMigrator      # detener
sc start DicomMigrator     # arrancar
sc delete DicomMigrator    # eliminar el servicio (tras detenerlo)
```

> Recuperación automática: para que Windows reinicie el servicio si falla, en
> services.msc → DICOM Migrator → Propiedades → pestaña "Recuperación", configura
> "Reiniciar el servicio" en los primeros/segundos fallos.

### Si el servicio no arranca

Si `sc query DicomMigrator` muestra que el servicio se detuvo o no llega a `RUNNING`,
revisa los logs de la aplicación, que se escriben junto al ejecutable:

```bash
type C:\DicomMigrador\logs\dicommigrator-*.log
```

Causas más frecuentes:

- **Autenticación fallida (`28P01`)**: la variable `ConnectionStrings__Default` no se
  definió como variable de máquina (`setx /M`), o tiene una contraseña incorrecta.
  Recuerda que `setx` solo afecta a procesos creados *después*; si cambiaste la variable,
  recrea el servicio (`sc delete` + `sc create`) o reinicia para que la recoja.
- **PostgreSQL inaccesible**: el servidor no está arrancado o el host/puerto no son
  correctos en la cadena de conexión.
- **Puerto 5200 ocupado**: hay otra instancia de la app corriendo (a mano o como otro
  servicio) usando el mismo puerto.

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

## Ficheros de log

Los logs se escriben en la carpeta `logs/` junto al ejecutable (p. ej.
`C:\DicomMigrador\logs\dicommigrator-AAAAMMDD.log`). Su crecimiento está acotado por tres
mecanismos de Serilog, por lo que no pueden llenar el disco:

- **Rotación diaria**: un fichero nuevo por día (`dicommigrator-AAAAMMDD.log`).
- **Tope de tamaño por fichero**: 50 MB; si se supera en un mismo día, el log continúa en
  otro fichero en lugar de crecer sin límite.
- **Retención de 30 ficheros**: solo se conservan los 30 más recientes; los antiguos se
  borran automáticamente.

El techo de espacio es, por tanto, del orden de 30 × 50 MB ≈ 1,5 GB en el peor caso; en
uso normal, mucho menos. Como el límite es por número de ficheros (no de días) y un día de
migración masiva puede generar varios ficheros al partirse por tamaño, en ese escenario el
histórico cubrirá algo menos de 30 días.

Ajustes (en `src/DicomMigrator.Web/Program.cs`, sink `WriteTo.File`):

- `retainedFileCountLimit`: subir (p. ej. 60 ó 90) para conservar más histórico.
- `fileSizeLimitBytes`: tamaño máximo por fichero.
- Verbosidad: las migraciones registran cada C-MOVE y verificación a nivel `INFO`, lo que
  en migraciones de cientos de miles de estudios genera muchas líneas. Como ese detalle ya
  queda en la auditoría (en la BD), puede reducirse el tamaño de los logs subiendo el nivel
  mínimo del fichero o bajando esos mensajes a `Debug`.

## Auditoría (tabla AuditLogs)

La auditoría de migraciones se guarda en la base de datos, en la tabla `AuditLogs` (una
entrada por estudio procesado). Es la tabla que más rápido crece, por lo que tiene una
purga automática que evita el crecimiento sin límite:

- **Retención de 90 días** (configurable). El servicio de mantenimiento en segundo plano
  borra periódicamente las entradas antiguas. La cadencia se registra al arrancar
  (p. ej. `retención=90d · intervalo=24h`: limpia al iniciar y luego cada 24 h).
- **Solo se purgan las entradas `INFO`**, que son el grueso del volumen (un "Verificado
  OK" por estudio) y pierden valor con el tiempo. Las entradas **`WARN` y `ERROR` se
  conservan** para poder diagnosticar incidencias históricas.

Ajustar la retención (sin tocar código) en `appsettings.json` / `appsettings.Production.json`:

```json
{
  "Maintenance": {
    "AuditLogRetentionDays": 90
  }
}
```

Notas:

- El `DELETE` de la purga libera las filas; el espacio en disco lo recupera el
  *autovacuum* de PostgreSQL (o el `VACUUM` del modo `--maintenance`), de forma automática.
- Las entradas `WARN`/`ERROR` no se purgan nunca. En un sistema sano son muy pocas, así que
  no suponen un problema de volumen; si se acumularan muchas, sería señal de fallos
  recurrentes que conviene investigar.

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
