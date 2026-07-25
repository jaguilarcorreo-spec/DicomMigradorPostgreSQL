# Despliegue de MOVE · DICOM Migrator (PostgreSQL)

Procedimiento reproducible para desplegar la aplicación contra una base PostgreSQL
limpia, siguiendo el **Modelo A**: el usuario de aplicación es dueño del esquema y
la propia app aplica las migraciones al arrancar.

> **Novedades a tener en cuenta al desplegar** (además de lo clásico de PostgreSQL):
> la aplicación ahora exige **autenticación** (usuario `admin` que se siembra solo) y
> requiere una **licencia válida** para poder ejecutar migraciones. Ambas cosas se
> cubren en las secciones 5 y 6. Para uso en red conviene además **HTTPS** (sección 9).

## Flujo de despliegue de un vistazo

1. Crear rol y base en PostgreSQL (sección 1).
2. Configurar la cadena de conexión (sección 2).
3. Publicar y arrancar la app / registrarla como servicio (secciones 4 y 8).
4. La app crea el esquema sola al arrancar (sección 3).
5. Primer acceso: entrar como `admin`, cambiar la contraseña, crear usuarios (sección 5).
6. Activar la licencia: obtener el fingerprint, pedir la licencia e instalarla (sección 6).
7. Configurar SCU local, nodos DICOM y ventanas (sección 7).
8. (Red) Configurar HTTPS (sección 9).

## Requisitos

- **PostgreSQL 16 o superior** (probado en 18).
- **.NET 9 SDK** (para compilar/publicar) y, si se generan migraciones, la herramienta
  `dotnet-ef` (`dotnet tool install --global dotnet-ef`).
- **Windows** para el despliegue como servicio (el fingerprint de licencia lee el
  firmware por WMI y, de respaldo, el registro; ambos son de Windows).
- Una **licencia** del proveedor para la máquina de destino (ver sección 6). Sin ella la
  app arranca y es navegable, pero **no ejecuta migraciones**.

## Modelo de roles (PostgreSQL)

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
fichero DIRECTAMENTE en la carpeta de despliegue (p. ej. `C:\DicomMigrator`), no en el
proyecto. El servicio corre en entorno Production, así que lo lee automáticamente y su
cadena gana sobre el placeholder. Está excluido de la publicación, de modo que NO se
sobrescribe al republicar. Este mismo fichero es un buen sitio para las claves
`Auth:InitialAdminPassword` y `License:Path` (secciones 5 y 6):

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
  },
  "Auth": {
    "InitialAdminPassword": "una-contraseña-inicial-mejor-que-admin"
  },
  "License": {
    "Path": "C:\\DicomMigrator\\license.dmlic"
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

En una base **vacía**, la app crea el esquema completo (todas las tablas, índices,
secuencias y datos semilla) a partir de las migraciones EF. Hay dos formas equivalentes;
elige una.

### Opción 3a — La app migra al arrancar (recomendada en Modelo A)

No hay que hacer nada especial: al iniciar, la app ejecuta `Migrate()` y crea/actualiza
el esquema usando la conexión configurada (usuario `dicom_app_migrator`). Simplemente
arranca la aplicación (paso 4).

> Nota: `Migrate()` es idempotente. En una base ya al día **no ejecuta ningún
> `CREATE`/`ALTER`**: solo comprueba la tabla `__EFMigrationsHistory` y sigue. No
> "recrea" nada, así que dejarlo activado no penaliza el arranque.

### Opción 3b — Aplicar migraciones explícitamente antes de arrancar

Útil en despliegues controlados. La herramienta `dotnet ef` usa la variable
`DICOMMIGRATOR_DESIGN_CONNSTR`; **defínela con el usuario de aplicación** para que las
tablas nazcan con el propietario correcto (si no la defines, cae por defecto al usuario
`postgres`, que dejaría las tablas a su nombre):

```bash
set DICOMMIGRATOR_DESIGN_CONNSTR=Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE

dotnet ef database update --project src/DicomMigrator.Infrastructure --startup-project src/DicomMigrator.Web
```

> Importante: aplica las migraciones como `dicom_app_migrator`, no como `postgres`.
> Si las aplica `postgres`, las tablas quedan a su nombre y la app (que corre como
> `dicom_app_migrator`) no podría crear índices ni evolucionar el esquema
> (error `42501: debe ser dueño de la tabla`).

> Requisito: las migraciones tienen que estar **compiladas en el binario** que
> despliegas. Si añades una migración nueva, vuelve a publicar desde ese código; de lo
> contrario el arranque falla con `PendingModelChangesWarning` (modelo con cambios sin
> migración).

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

## 5. Primer acceso y autenticación

La aplicación **exige identificarse**: ninguna pantalla es accesible sin sesión, y las
descargas de CSV/Excel también la requieren (contienen datos de paciente).

- **Usuario inicial:** en el primer arranque, si no hay ningún usuario, la app crea
  `admin` automáticamente con una contraseña inicial y la marca de **cambio obligatorio**.
  La contraseña inicial se toma de `Auth:InitialAdminPassword`; si no se define, es
  `admin`. **Defínela** en `appsettings.Production.json` (o variable de entorno
  `Auth__InitialAdminPassword`) antes del primer arranque, o cámbiala de inmediato.
- **Primer login:** entra en la web como `admin`, y la aplicación te forzará a cambiar la
  contraseña antes de dejarte usar nada más.
- **Roles:** hay tres — **Administrador** (todo: nodos, borrados, usuarios, configuración
  local, licencia), **Operador** (lanzar descubrimientos, migraciones y verificaciones;
  exportar) y **Consulta** (solo lectura y exportar). Los botones y menús que no
  corresponden al rol no se muestran.
- **Crear usuarios:** en **Usuarios** (solo Administrador) das de alta cuentas, asignas
  rol, reseteas contraseñas y desbloqueas cuentas. El nombre de acceso no se puede cambiar
  una vez creado (lo referencia la auditoría). Contraseñas: mínimo 8 caracteres, se
  almacenan solo como hash.
- **Bloqueo:** tras **5 intentos fallidos** seguidos la cuenta se bloquea **15 minutos**;
  un administrador puede liberarla antes desde *Usuarios*.

## 6. Licencia

La aplicación requiere una **licencia válida** para operar. Verifica la licencia al
arrancar y la gestiona la pantalla **Licencia** (solo Administrador).

- **Sin licencia válida:** la app **arranca y es navegable** —para poder instalarla—, pero
  **no se inician migraciones**; un banner rojo lo avisa en todas las pantallas. El
  descubrimiento y la verificación no se bloquean.

### 6.1 Obtener el fingerprint de la máquina

Cada equipo tiene un identificador único y estable (`XXXX-XXXX-XXXX-XXXX-XXXX`), derivado
del firmware (UUID de sistema y serie de placa base). **Sobrevive a reinstalar o clonar
Windows** y solo cambia si se sustituye la placa base.

Dos formas de obtenerlo:

```bash
C:\DicomMigrator\DicomMigrator.Web.exe --fingerprint
```

(imprime el valor y sale; no necesita base de datos ni configuración), o desde la pantalla
**Licencia** una vez dentro de la app.

### 6.2 Pedir e instalar la licencia

1. Envía el fingerprint al proveedor, que emite una licencia (perpetua o temporal) ligada
   a esa máquina.
2. Instálala de una de estas dos formas:
   - **Interfaz:** en la pantalla **Licencia**, pega el token (empieza por `DMLIC1.`) y
     pulsa **Instalar**.
   - **Fichero (desatendido):** coloca el `.dmlic` en la ruta indicada por `License:Path`
     en `appsettings.Production.json`; al arrancar se importa solo si aún no hay licencia
     instalada.
3. Al instalar se comprueba, en orden: firma criptográfica (Ed25519, con clave pública
   embebida en la app — no hay que configurar nada), que sea de este producto, la vigencia,
   el binding de máquina y el anti-rollback. Solo se activa si es utilizable en ese momento.

### 6.3 Notas de operación

- **Máquina nueva / servidor nuevo:** el fingerprint será distinto, así que necesitarás
  una licencia nueva para esa máquina. Copiar la base de datos NO transfiere la validez a
  otra máquina si la licencia está ligada.
- **Anti-rollback:** cada licencia lleva un número de serie creciente; no se admite instalar
  una con serie inferior a la ya activada. Además, retrasar el reloj del sistema no revive
  una licencia caducada. El estado se guarda en la tabla `LicenseState` (una fila).
- **Copia de seguridad:** la licencia instalada vive en la base de datos; si restauras un
  backup de la misma máquina, la licencia y su estado anti-rollback viajan con él.

## 7. Configuración DICOM inicial

Con la sesión iniciada y la licencia activa, configura el entorno DICOM. Estos ajustes se
guardan en PostgreSQL y se aplican en caliente:

- **SCU local**: AET y puerto de recepción del Storage SCP local (necesario para recibir
  las instancias del C-MOVE). Estos datos hay que registrarlos también en el PACS de origen.
- **Nodos DICOM**: dar de alta el PACS de origen y el de destino (AET, host, puerto y, si
  aplica, DICOMweb), y comprobarlos con un C-ECHO desde **Diagnóstico**.
- **Ventanas de ejecución**: si la migración debe limitarse a ciertas franjas horarias.

A partir de ahí ya se pueden lanzar descubrimientos (Discovery) y migraciones.

## 8. Ejecutar como Servicio de Windows (modo desatendido)

Para que la aplicación arranque sola con el sistema, sobreviva a reinicios y no dependa
de una consola abierta, se registra como Servicio de Windows. La app ya incluye el
soporte (`UseWindowsService`); solo hay que publicarla y registrar el servicio.

**Antes de empezar:**

- Detén cualquier instancia de la app que esté corriendo a mano (Ctrl+C), porque el
  servicio usará el mismo puerto (5200 por defecto).
- Los pasos requieren una consola **abierta como administrador**.
- PostgreSQL debe estar accesible con el usuario `dicom_app_migrator` (secciones 1 y 2).

### 8.1 Publicar el ejecutable autónomo

```bash
dotnet publish src/DicomMigrator.Web -c Release -r win-x64 --self-contained true -o C:\DicomMigrator
```

Esto deja el `.exe` y sus dependencias en `C:\DicomMigrator` (elige la ruta que prefieras).

### 8.2 Configurar la conexión para el servicio

Un servicio no hereda las variables de entorno de tu sesión. Define la cadena de
conexión de forma persistente: o bien un `appsettings.Production.json` junto al `.exe`
(recomendado; ver sección 2, opción A), o una variable de entorno **de máquina** (no de
usuario), como administrador:

```bash
setx /M ConnectionStrings__Default "Host=SERVIDOR;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=CONTRASEÑA_FUERTE"
```

(El `appsettings.Development.json` NO se usa aquí: el servicio corre en entorno
Production.)

### 8.3 Crear el servicio

Como administrador (los espacios tras `binPath=` y `start=` son obligatorios en `sc`):

```bash
sc create DicomMigrator binPath= "C:\DicomMigrator\DicomMigrator.Web.exe" start= auto DisplayName= "DICOM Migrator"
sc description DicomMigrator "Migración de estudios DICOM entre sistemas PACS."
```

### 8.4 Arrancar y verificar

```bash
sc start DicomMigrator
sc query DicomMigrator
```

Debe aparecer `STATE: 4 RUNNING`. La interfaz queda en la URL de Kestrel
(por defecto `http://localhost:5200`). Los logs van a `C:\DicomMigrator\logs\`.

### 8.5 Gestión del servicio

```bash
sc stop DicomMigrator      # detener
sc start DicomMigrator     # arrancar
sc delete DicomMigrator    # eliminar el servicio (tras detenerlo)
```

> Recuperación automática: para que Windows reinicie el servicio si falla, en
> services.msc → DICOM Migrator → Propiedades → pestaña "Recuperación", configura
> "Reiniciar el servicio" en los primeros/segundos fallos.

### 8.6 Si el servicio no arranca

Si `sc query DicomMigrator` muestra que el servicio se detuvo o no llega a `RUNNING`,
revisa los logs de la aplicación, que se escriben junto al ejecutable:

```bash
type C:\DicomMigrator\logs\dicommigrator-*.log
```

Causas más frecuentes:

- **Autenticación fallida (`28P01`)**: la variable `ConnectionStrings__Default` no se
  definió como variable de máquina (`setx /M`), o tiene una contraseña incorrecta.
  `setx` solo afecta a procesos creados *después*; si cambiaste la variable, recrea el
  servicio (`sc delete` + `sc create`) o reinicia para que la recoja.
- **Migración pendiente (`PendingModelChangesWarning`)**: el binario publicado no incluye
  alguna migración del modelo. Vuelve a publicar desde el código actualizado.
- **PostgreSQL inaccesible**: el servidor no está arrancado o el host/puerto no son
  correctos en la cadena de conexión.
- **Puerto 5200 ocupado**: hay otra instancia de la app corriendo (a mano o como otro
  servicio) usando el mismo puerto.

## 9. HTTPS (acceso desde otros equipos)

Si la aplicación se usa **desde otros equipos**, la conexión debe ir cifrada: de lo
contrario, la contraseña de acceso y los datos de paciente viajan en claro. La pantalla de
login avisa en ámbar cuando la conexión no es segura.

Se activa por configuración, sin tocar código: define un endpoint `Https` con su
certificado en la sección `Kestrel` de `appsettings.Production.json`. La redirección
automática a HTTPS se activa sola en cuanto se detecta un endpoint Https (o la variable
`ASPNETCORE_HTTPS_PORT`).

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5443",
        "Certificate": { "Path": "C:\\DicomMigrator\\cert.pfx", "Password": "CLAVE_DEL_PFX" }
      }
    }
  }
}
```

> Este cifrado es el de **la interfaz web**, independiente del TLS de DICOM que se
> configura por nodo. Para acceder desde la red por HTTP, cambia `localhost` por `0.0.0.0`
> o la IP de la máquina en el endpoint `Http` y reinicia.

## 10. Modos de línea de comandos

El ejecutable admite varios modos que terminan sin levantar el servidor web:

```bash
DicomMigrator.Web.exe --fingerprint        # imprime el fingerprint de la máquina y sale
DicomMigrator.Web.exe --maintenance        # VACUUM + ANALYZE de la base y sale
DicomMigrator.Web.exe --maintenance-full   # equivalente a --maintenance
```

(En desarrollo, con `dotnet run --project src/DicomMigrator.Web -- --maintenance`.)

## 11. Generar nuevas migraciones (al evolucionar el modelo)

Cuando cambie el modelo de datos, genera una migración (con la variable de diseño
apuntando a `dicom_app_migrator`, igual que en 3b):

```bash
dotnet ef migrations add NombreDescriptivo --project src/DicomMigrator.Infrastructure --startup-project src/DicomMigrator.Web
```

La migración se aplica luego con la opción 3a (al arrancar) o 3b (explícita). Recuerda
**republicar** el binario para que la migración viaje con él al despliegue.

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
`C:\DicomMigrator\logs\dicommigrator-AAAAMMDD.log`). Su crecimiento está acotado por tres
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
  mínimo del fichero (Serilog) o bajando esos mensajes a `Debug`.

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
- Las entradas `WARN`/`ERROR` no se purgan nunca. En un sistema sano son muy pocas.
- Los procesos automáticos quedan atribuidos en la auditoría a la persona que los lanzó
  (p. ej. `WORKER (por admin)`), gracias al control de acceso.

## Resolución de problemas

- **`28P01: la autentificación password falló`** — usuario o contraseña incorrectos en
  la cadena de conexión. Verifica con `ALTER ROLE ... WITH PASSWORD` y revisa qué fichero
  de configuración (o variable de entorno) está usando la app.
- **`PendingModelChangesWarning` al arrancar** — el modelo tiene cambios sin migración, o
  el binario no incluye la última migración. Genera la migración (sección 11) y/o vuelve a
  publicar desde el código actualizado.
- **`42501: debe ser dueño de la tabla`** — las migraciones se aplicaron con un usuario
  distinto al que ejecuta la app. Solución de raíz: recrear la base con
  `OWNER dicom_app_migrator` y aplicar las migraciones como ese usuario.
- **`55006: no se puede eliminar la base de datos activa`** — hay conexiones abiertas a
  la base. Ejecuta el `DROP` desde la base `postgres` (no desde la base a borrar) y, si
  hace falta, cierra las sesiones con
  `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'dicommigrator' AND pid <> pg_backend_pid();`
- **No arrancan las migraciones y hay banner rojo de licencia** — no hay licencia válida
  instalada. Instala una (sección 6). El descubrimiento y la verificación sí funcionan.
- **Licencia "Ligada a otra máquina"** — el fingerprint no coincide (típico al desplegar en
  un servidor nuevo o tras cambiar la placa base). Obtén el fingerprint actual con
  `--fingerprint` y pide una licencia nueva para esa máquina.
- **Cuenta bloqueada por intentos fallidos** — con otro administrador, desbloquéala desde
  *Usuarios*. Sin otro administrador, desde PostgreSQL:
  `UPDATE "AppUsers" SET "FailedAttempts"=0, "LockedUntil"=NULL WHERE "UserName"='admin';`
- **Contraseña de `admin` perdida y no hay otro administrador** — no se guarda en claro y no
  hay auto-servicio de reseteo. La contraseña no se puede fijar por SQL (el hash es PBKDF2,
  no se teclea a mano). Recuperación de emergencia con acceso a PostgreSQL: vaciar la tabla
  de usuarios y reiniciar la app, que vuelve a sembrar `admin` con `Auth:InitialAdminPassword`:
  `DELETE FROM "AppUsers";` (⚠ borra también el resto de cuentas; habrá que recrearlas. La
  auditoría histórica que referencia esos nombres se conserva, pero ya no se re-vincula a un
  usuario existente). Si conservas algún otro administrador, es preferible resetear la
  contraseña de `admin` desde *Usuarios* con esa cuenta.
