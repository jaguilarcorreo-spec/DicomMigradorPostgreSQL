MOVE · DICOM Migrator 1.8.1 · PostgreSQL 16+ · EF Core 9

# Base de datos de DICOM Migrator

Esquema físico, índices, ciclo de vida de los estados y flujo de datos entre tablas. El esquema sale del snapshot de migraciones EF (20 migraciones, la última `PausaPorVentana`) y lo he contrastado con la base local. Los estados y flujos salen del código de los servicios y repositorios.

**16** tablas **9** claves foráneas **9** relaciones sin clave foránea **23** índices · 5 únicos · 3 parciales **5** tablas de una sola fila

[1 · Esquema físico](#er) [2 · Índices](#indices) [3 · Estados](#estados) [4 · Flujo de datos](#flujo)

## 1 · Esquema físico

AppDbContextModelSnapshot

Las 16 tablas con todas sus columnas y tipos de PostgreSQL, en tres bloques. Las tablas de otro bloque aparecen reducidas a las columnas que intervienen en la relación.

── clave foránea real, con su acción al borrar ┄┄ relación solo en el código, sin clave foránea PK · FK · UK clave primaria, foránea, o parte de un índice único null admite nulos

### Nodos, seguridad y configuración

Tablas sin relaciones entre sí. `LocalConfigurations`, `LicenseStates` y `NotificationSettings` tienen una sola fila; `LicenseStates` y `NotificationSettings` usan un Id fijo, sin secuencia.

```mermaid
erDiagram
DicomNodes {
  int Id PK
  varchar(100) Alias
  int AssociationTimeoutSeconds
  text AuthType
  text AuthUsername "null"
  timestamptz CreatedAt
  text Description
  text EncryptedSecret "null · EN CLARO"
  bool HasDicomWeb
  int HttpTimeoutSeconds
  bool IsActive
  varchar(16) LocalAet
  int MaxConcurrentAssociations
  text NodeType
  int OperationTimeoutSeconds
  text QidoBaseUrl "null"
  varchar(16) RemoteAet
  text RemoteHost
  int RemotePort
  text StowBaseUrl "null"
  timestamptz UpdatedAt
  bool UseTls
  bool ValidateTls
  text WadoBaseUrl "null"
  text WebBaseUrl "null"
  text WebQidoPath "null"
  text WebStowPath "null"
  text WebWadoPath "null"
}
LocalConfigurations {
  int Id PK
  text Description
  varchar(16) LocalAet
  text LocalHostname
  int LocalPort
  int MaxConcurrentMigrations
  timestamptz UpdatedAt
}
AppUsers {
  int Id PK
  timestamptz CreatedDate
  varchar(120) DisplayName "null"
  int FailedAttempts
  bool IsActive
  timestamptz LastLoginDate "null"
  timestamptz LockedUntil "null"
  bool MustChangePassword
  varchar(256) PasswordHash
  varchar(20) Role
  varchar(64) SecurityStamp
  varchar(64) UserName UK
}
LicenseStates {
  int Id PK
  bigint ActivatedSerial
  timestamptz ActivatedUtc "null"
  timestamptz ExpiresUtc "null"
  bigint HighestSerial
  timestamptz LastSeenUtc
  varchar(64) LicId "null"
  text Token "null"
  timestamptz UpdatedUtc
}
NotificationSettings {
  int Id PK
  text BaseUrl "null"
  bool Enabled
  text FromAddress "null"
  bool NotifyAutoPaused
  bool NotifyCaptureFinished
  bool NotifyDiscoveryCompleted
  bool NotifyDiscoveryFailed
  bool NotifyMigrationCompleted
  bool NotifyMigrationFailed
  bool NotifyPopulateFinished
  bool NotifyVerificationCompleted
  bool NotifyVerificationFailed
  text Recipients "null"
  text SmtpHost "null"
  text SmtpPassword "null · EN CLARO"
  int SmtpPort
  bool SmtpUseStartTls
  text SmtpUser "null"
  timestamptz UpdatedUtc
}
NotificationOutbox {
  bigint Id PK
  int Attempts
  text BodyHtml
  timestamptz CreatedUtc
  varchar(40) EventKind
  timestamptz LastAttemptUtc "null"
  text LastError "null"
  text Recipients
  timestamptz SentUtc "null"
  varchar(20) Status
  text Subject
}
```

### Inventario (descubrimiento)

Un job recorre el PACS por particiones (día; si la respuesta llega truncada, franjas horarias cada vez más cortas y, en último caso, modalidad) y guarda cada estudio una vez por job. La captura de nivel 2 añade los SOPInstanceUID de cada estudio.

```mermaid
erDiagram
DicomNodes {
  int Id PK
  varchar(100) Alias
}
DiscoveryJobs {
  int Id PK
  int SourcePacsId FK
  timestamptz CaptureFinishedDate "null"
  timestamptz CaptureStartedDate "null"
  text CaptureStatus
  text CreatedBy "null"
  timestamptz CreatedDate
  text Description "null"
  varchar(20) DiscoveryType
  date EndDate "null"
  timestamptz FinishedDate "null"
  text Modalities "null"
  varchar(200) Name
  int PacsResultLimit
  varchar(10) QueryMethod
  date StartDate "null"
  timestamptz StartedDate "null"
  varchar(20) Status
  int WorkerThreads
}
DiscoveryPartitions {
  int Id PK
  int DiscoveryJobId FK
  int AttemptCount
  float8 DurationMs "null"
  date EndDate "null"
  timestamptz FinishedAt "null"
  text LastError "null"
  timestamptz LockDate "null"
  text LockedByWorker "null"
  varchar(16) Modality "null"
  varchar(20) PartitionType
  date StartDate "null"
  timestamptz StartedAt "null"
  varchar(20) Status
  int StudiesFound
  int StudiesInserted
  int StudiesUpdated
  text StudyTimeFrom "null"
  text StudyTimeTo "null"
}
DiscoveryRequests {
  bigint Id PK
  int DiscoveryJobId "ref. sin FK"
  int PartitionId "null · ref. sin FK"
  int SourcePacsId "ref. sin FK"
  int Attempt
  float8 DurationMs
  text Error "null"
  text Filters "null"
  varchar(10) QueryType
  timestamptz RequestDate
  varchar(10) Result
  int StudiesReturned
}
DiscoveredStudies {
  bigint Id PK
  int DiscoveryJobId UK "null · ref. sin FK"
  int PartitionId "null · ref. sin FK"
  int SourcePacsId "ref. sin FK"
  text AccessionNumber "null"
  timestamptz DiscoveryDate
  text InstitutionName "null"
  text IssuerOfPatientId "null"
  timestamptz LastUpdatedDate "null"
  text ModalitiesInStudy "null"
  int NumberOfStudyRelatedInstances "null"
  int NumberOfStudyRelatedSeries "null"
  text PatientBirthDate "null"
  text PatientId "null"
  text PatientName "null"
  text PatientSex "null"
  text RetrieveAETitle "null"
  text StudyDate "null"
  text StudyDescription "null"
  varchar(64) StudyInstanceUid UK
  text StudyTime "null"
}
DiscoveredInstances {
  bigint Id PK
  bigint DiscoveredStudyId FK, UK
  varchar(64) SeriesInstanceUid
  varchar(64) SopInstanceUid UK
}
DicomNodes ||--o{ DiscoveryJobs : "SourcePacsId · RESTRICT"
DiscoveryJobs ||--o{ DiscoveryPartitions : "CASCADE"
DiscoveredStudies ||--o{ DiscoveredInstances : "CASCADE"
DiscoveryJobs |o..o{ DiscoveredStudies : "DiscoveryJobId · sin FK"
DiscoveryPartitions |o..o{ DiscoveredStudies : "PartitionId · sin FK"
DicomNodes ||..o{ DiscoveredStudies : "SourcePacsId · sin FK"
DiscoveryJobs ||..o{ DiscoveryRequests : "DiscoveryJobId · sin FK"
DiscoveryPartitions |o..o{ DiscoveryRequests : "PartitionId · sin FK"
```

### Migración

Una migración copia estudios del inventario (o los importa directamente) y guarda en `MigrationInstances` los UID de origen para la verificación de nivel 2.

```mermaid
erDiagram
DicomNodes {
  int Id PK
  varchar(100) Alias
}
DiscoveryJobs {
  int Id PK
  varchar(200) Name
}
DiscoveredStudies {
  bigint Id PK
  int DiscoveryJobId UK "null · ref. sin FK"
  varchar(64) StudyInstanceUid UK
}
Migrations {
  int Id PK
  int DestNodeId FK
  int OriginNodeId FK
  int PopulateSourceJobId "null · ref. sin FK"
  timestamptz CreatedAt
  text CreatedBy
  text Description
  varchar(20) DiscoveryMethod
  timestamptz FinishedAt "null"
  text InventoryDateFrom "null"
  text InventoryDateTo "null"
  int MaxRetries
  bool MigrationAutoPaused
  text ModalityPriority
  varchar(200) Name
  bool OldestFirst
  bool PausedByWindow
  int PopulateDone
  text PopulateError "null"
  text PopulateStatus
  int PopulateTotal
  int RetryDelaySeconds
  date StartFromDate "null"
  timestamptz StartedAt "null"
  varchar(20) Status
  varchar(30) TransferMethod
  timestamptz UpdatedAt
  bool VerificationAutoPaused
  int VerificationLevel
  text VerificationStatus
  int WorkerThreads
}
ExecutionWindows {
  int Id PK
  int MigrationId FK
  bool AllDay
  text EnabledDays
  time EndTime
  varchar(20) Kind
  time StartTime
  varchar(50) TimeZoneId
}
MigrationStudies {
  bigint Id PK
  int MigrationId FK, UK
  text AccessionNumber "null"
  timestamptz DiscoveryDate
  text IssuerOfPatientId "null"
  text LastError "null"
  timestamptz LastUpdateDate
  timestamptz LockDate "null"
  text LockedByWorker "null"
  timestamptz MigrationDate "null"
  timestamptz MigrationStartDate "null"
  varchar(30) MigrationStatus
  text ModalitiesInStudy "null"
  smallint ModalityRank
  text PatientId "null"
  int RetryCount
  int SourceInstanceCount "null"
  int SourceSeriesCount "null"
  text StudyDate "null"
  varchar(64) StudyInstanceUid UK
  int TargetInstanceCount "null"
  int TargetSeriesCount "null"
  timestamptz VerificationDate "null"
  timestamptz VerificationStartDate "null"
  text VerifiedBy "null"
  int VerifyExtraCount
  timestamptz VerifyLockDate "null"
  text VerifyLockedByWorker "null"
  int VerifyMissingCount
  text VerifyMissingUids "null"
  int VerifyRetryCount
}
MigrationInstances {
  bigint Id PK
  bigint MigrationStudyId FK, UK
  varchar(64) SeriesInstanceUid
  varchar(64) SopInstanceUid UK
}
AuditLogs {
  bigint Id PK
  int MigrationId FK
  varchar(40) Action
  varchar(10) Level
  varchar(10) Result
  text StudyInstanceUid "null"
  text TechnicalMessage "null"
  timestamptz Timestamp
  text UserOrProcess "null"
}
DicomNodes ||--o{ Migrations : "OriginNodeId · RESTRICT"
DicomNodes ||--o{ Migrations : "DestNodeId · RESTRICT"
Migrations ||--o{ ExecutionWindows : "CASCADE"
Migrations ||--o{ MigrationStudies : "CASCADE"
Migrations ||--o{ AuditLogs : "CASCADE"
MigrationStudies ||--o{ MigrationInstances : "CASCADE"
DiscoveryJobs |o..o{ Migrations : "PopulateSourceJobId · sin FK"
DiscoveredStudies |o..o{ MigrationStudies : "copia por StudyInstanceUid"
```

`StudyDate` se guarda como texto DICOM `YYYYMMDD`, no como fecha: el orden y los filtros por fecha dependen de que el PACS siempre devuelva ese formato exacto (lo advierte el propio código de `AcquireNextPendingAsync`).

`ModalityRank` es la posición de la primera modalidad del estudio en la lista de prioridad de la migración (999 si no está). Se recalcula al iniciar o reanudar la migración, solo en las filas `Pending` o `RetryPending` cuyo valor cambia, y es la primera columna del orden de la cola.

## 2 · Índices

23 índices

Además de las claves primarias. Los marcados como *convención* no están en `AppDbContext`: EF los crea solo para cada clave foránea.

| Tabla | Índice | Columnas | Tipo | Para qué sirve |
| --- | --- | --- | --- | --- |
| MigrationStudies | IX_MigrationStudies_MigrationId_StudyInstanceUid | MigrationId, StudyInstanceUid | único | Un estudio no puede estar dos veces en la misma migración. |
| MigrationStudies | IX_MigrationStudies_MigrationId_MigrationStatus | MigrationId, MigrationStatus | normal | Recuentos por estado del panel y comprobaciones de trabajo pendiente. |
| MigrationStudies | IX_MigStudies_queue_newest | MigrationId, ModalityRank, RetryCount, StudyDate DESC, Id | parcial | Solo filas `Pending`. Cola de migración con los estudios más recientes primero: el worker toma el primero libre recorriendo el índice en orden, sin ordenar los pendientes. |
| MigrationStudies | IX_MigStudies_queue_oldest | MigrationId, ModalityRank, RetryCount, StudyDate, Id | parcial | Igual, para las migraciones con «más antiguos primero». |
| MigrationStudies | IX_MigStudies_verify_queue | MigrationId, Id | parcial | Solo filas `Migrated`: cola de verificación. |
| MigrationStudies | IX_MigStudies_Mig_Patient | MigrationId, PatientId | normal | Búsqueda por paciente en la tabla de migración. |
| MigrationStudies | IX_MigStudies_Mig_Accession | MigrationId, AccessionNumber | normal | Búsqueda por número de acceso. |
| MigrationStudies | IX_MigStudies_Mig_DiscDate | MigrationId, DiscoveryDate | normal | Orden y paginación por fecha de alta. |
| MigrationInstances | IX_MigInstances_Study_Sop | MigrationStudyId, SopInstanceUid | único | UID de origen por estudio para la verificación de nivel 2; evita duplicados al copiar. |
| DiscoveredStudies | IX_DiscoveredStudies_DiscoveryJobId_StudyInstanceUid | DiscoveryJobId, StudyInstanceUid | único | Un estudio una sola vez por job: es la clave del upsert del descubrimiento. |
| DiscoveredStudies | IX_DiscoveredStudies_SourcePacsId_StudyDate | SourcePacsId, StudyDate | normal | Rango de fechas del inventario por PACS de origen. |
| DiscoveredStudies | IX_DiscoveredStudies_PartitionId | PartitionId | normal | Estadísticas de estudios por partición. |
| DiscoveredInstances | IX_DiscInstances_Study_Sop | DiscoveredStudyId, SopInstanceUid | único | UID capturados por estudio (nivel 2), sin duplicados. |
| DiscoveryPartitions | IX_DiscoveryPartitions_DiscoveryJobId_Status | DiscoveryJobId, Status | normal | Los workers toman la siguiente partición `Pending` del job. |
| DiscoveryRequests | IX_DiscoveryRequests_DiscoveryJobId | DiscoveryJobId | normal | Registro de consultas al PACS por job. |
| AuditLogs | IX_AuditLogs_MigrationId_Timestamp | MigrationId, Timestamp | normal | Auditoría de una migración ordenada por fecha. |
| AuditLogs | IX_AuditLogs_Timestamp | Timestamp | normal | Purga de entradas antiguas y vista global reciente. |
| AppUsers | IX_AppUsers_UserName | UserName | único | Inicio de sesión; nombres de usuario no repetidos. |
| NotificationOutbox | IX_NotificationOutbox_Status_Id | Status, Id | normal | El repartidor toma los correos `Pending` en orden de llegada. |
| Migrations | IX_Migrations_OriginNodeId | OriginNodeId | convención | Clave foránea al nodo de origen. |
| Migrations | IX_Migrations_DestNodeId | DestNodeId | convención | Clave foránea al nodo de destino. |
| ExecutionWindows | IX_ExecutionWindows_MigrationId | MigrationId | convención | Clave foránea a la migración. |
| DiscoveryJobs | IX_DiscoveryJobs_SourcePacsId | SourcePacsId | convención | Clave foránea al PACS de origen. |

- **Cola de migración:** los dos índices tienen las mismas columnas con distinto orden de fecha, por eso llevan nombre propio en `AppDbContext` (sin él, EF los fundiría en uno). Los `RetryPending` no están en ellos: se toman con una segunda consulta solo cuando no queda ningún `Pending`.
- **Sin índice a propósito:** `ModalitiesInStudy`. El filtro de modalidad busca por subcadena (filtrar por `CT` trae también `RTSTRUCT`), y un B-tree no resuelve búsquedas en mitad del texto; `pg_trgm` necesitaría al menos tres caracteres. El filtro va casi siempre acotado por job, que sí usa índice.

## 3 · Ciclo de vida de los estados

Repositorios y servicios

Valores reales que el código asigna a cada columna de estado y qué los provoca. Las transiciones de rescate devuelven a la cola el trabajo que se quedó a medias si el proceso muere de golpe.

### MigrationStudies.MigrationStatus

```mermaid
stateDiagram-v2
  [*] --> Pending : poblado o importación
  Pending --> Queued : un worker lo adquiere y lo bloquea
  RetryPending --> Queued : pasado RetryDelaySeconds, si no queda ningún Pending
  Queued --> Pending : bloqueo caducado (10 min) o rescate al iniciar
  Queued --> Migrating : empieza el C-MOVE, conserva el bloqueo
  Migrating --> Migrated : C-MOVE correcto
  Migrating --> RetryPending : falla y quedan intentos
  Migrating --> Failed : falla sin intentos
  Migrating --> Pending : pausa o error de conexión
  Migrating --> Pending : 15 min sin latido o rescate al iniciar
  Failed --> RetryPending : Reintentar fallidos
  Migrated --> VerificationPending : un verificador lo adquiere
  VerifyRetryPending --> VerificationPending : pasado el retardo
  VerificationPending --> Migrated : destino inaccesible, pausa o bloqueo caducado
  VerificationPending --> Verified : cuadra con el origen
  VerificationPending --> VerifyRetryPending : no cuadra o el destino responde con error, y quedan intentos
  VerificationPending --> VerifyFailed : no cuadra o el destino responde con error, sin intentos
  VerifyFailed --> Migrated : Reintentar verificación
  Verified --> [*]
```

- **Adquisición atómica:** un solo `UPDATE … WHERE Id = (SELECT … ORDER BY … LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING *` pasa el estudio a `Queued` (o a `VerificationPending` en la verificación) y lo bloquea. Cada worker obtiene uno distinto sin esperar a los demás. Orden de la cola de migración: `ModalityRank`, `RetryCount`, `StudyDate` (según «más antiguos primero») e `Id`.
- **Rescate tras una caída:** `MarkMigratingAsync` conserva `LockedByWorker` y `LockDate`, y un latido renueva `LockDate` cada minuto mientras dura el C-MOVE. Un estudio en `Migrating` sin latido durante 15 minutos vuelve a `Pending`. Además, al iniciar o reanudar la migración, `ReleaseOrphanMigrationLocksAsync` devuelve a `Pending` todos los `Queued` y `Migrating`, tras esperar hasta 60 s a que terminen los workers de la ejecución anterior. Ninguno de los dos rescates consume reintentos.
- **Workers con el mismo nombre:** los nombres (`WORKER-1`…) se repiten entre migraciones, así que la liberación de bloqueos al terminar un worker filtra también por migración.
- **Acciones manuales:** «Cancelar» lleva el estudio a `Cancelled`; «Reiniciar» devuelve `Failed`, `Cancelled`, `Verified` o `Migrated` a `Pending`.
- **Contadores:** `RetryCount` sube al pasar a `RetryPending`; «Reintentar fallidos» no lo reinicia, así que da un solo intento más. `VerifyRetryCount` sube en cada verificación fallida y «Reintentar verificación» lo pone a 0.
- **Verificación sin respuesta frente a respuesta con error:** si el destino no responde (caído, red, HTTP 502/503/504/429/408), el estudio vuelve a `Migrated` sin gastar `VerifyRetryCount`. Si responde con un error a la consulta del estudio (estado DIMSE de fallo, HTTP 400/413/500…), cuenta como intento fallido.
- `VerifiedBy` no es un estado: indica qué comprobación se aplicó (`UidSet`, `Counts` o `ExistenceOnly`).

### Migrations.Status

```mermaid
stateDiagram-v2
  [*] --> Draft : creada
  Draft --> Running : Iniciar
  Running --> Paused : Pausar o auto-pausa por conexión
  Running --> Paused : ventana cerrada, PausedByWindow = true
  Running --> Paused : error de configuración (0xA801, rechazo permanente)
  Running --> Paused : los workers salen con estudios por migrar
  Paused --> Running : Reanudar o auto-reanudar
  Paused --> Running : ventana abierta o sin ventanas, solo con PausedByWindow
  Running --> Migrated : todo migrado sin fallos
  Running --> Failed : quedan estudios Failed
  Migrated --> Completed : no queda nada por migrar ni por verificar
  Running --> Cancelled : Parar
  Paused --> Cancelled : Parar
  Failed --> Running : Iniciar
  Cancelled --> Running : Iniciar
  Completed --> [*]
```

El comentario del modelo lista `Ready`, pero ningún código lo asigna; y omite `Migrated`, que sí se usa («migrada, pendiente de verificar»).

- **Auto-pausa por conexión** (`MigrationAutoPaused = true`): 5 fallos transitorios seguidos (origen caído o saturado, destino que no responde). La auto-reanudación comprueba con C-ECHO el origen y el destino; si se reanuda y vuelve a pausarse sin migrar nada, no se repite el correo y cada intento espera el doble (1, 2, 4… hasta 30 min).
- **Error de configuración:** pausa sin `MigrationAutoPaused`, así que no se reanuda sola.
- **Ventana horaria** (`PausedByWindow`): el planificador mira el estado cada minuto, no la transición. Si la migración está en `Running` con la ventana cerrada, la pausa y pone `PausedByWindow = true`; si está en `Paused` con esa marca y la ventana abierta (o ya no tiene ventanas), la reanuda. Cualquier inicio, reanudación o pausa manual borra la marca, así que una pausa manual no se reanuda al abrirse la ventana. Al estar en la base, sobrevive a un reinicio.
- **Paso a `Completed`:** lo hace quien termine último. La verificación, al acabar sin nada pendiente con la migración ya en `Migrated`; o la migración, al terminar con la verificación al día.

### Migrations.VerificationStatus

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> Running : Iniciar verificación
  Running --> Paused : Pausar, auto-pausa o error de configuración
  Running --> Paused : los workers salen con estudios por verificar
  Paused --> Running : Reanudar o auto-reanudar
  Running --> Completed : nada que verificar y la migración no está en marcha
  Running --> Idle : Parar
  Paused --> Idle : Parar
  Completed --> Running : Iniciar verificación
  Completed --> Running : la migración se inicia, se reanuda o termina
```

Va por separado de `Status`: se puede migrar y verificar a la vez.

- **Espera:** con la migración en `Running` y estudios por migrar, los verificadores no salen al vaciar su cola; esperan (sondeo cada 30 s) a que lleguen más `Migrated`.
- **`Completed` puede significar «al día»:** si la verificación termina con estudios aún por migrar (migración en pausa o sin iniciar), queda en `Completed` sin correo ni promoción, y la migración la relanza al iniciarse, reanudarse o terminar. Una verificación en `Paused` o `Idle` no se relanza sola.
- **Auto-pausa** (`VerificationAutoPaused = true`): la auto-reanudación comprueba el destino con el protocolo con el que se verifica (QIDO-RS si tiene DICOMweb, C-ECHO si no). Credenciales o AE Title rechazados, un HTTP 404 en la búsqueda QIDO-RS (URL mal configurada) o 5 estudios seguidos con error de consulta pausan sin auto-pausa: no se reanuda sola.

### DiscoveryJobs.Status

```mermaid
stateDiagram-v2
  [*] --> Draft : creado
  Draft --> Running : Iniciar
  Running --> Paused : Pausar
  Paused --> Running : Reanudar
  Running --> Completed : todas las particiones consultadas
  Running --> Paused : quedan particiones sin consultar
  Running --> Paused : error inesperado que detiene los workers
  Draft --> Completed : importación CSV
  Completed --> [*]
```

Si los workers terminan y aún quedan particiones `Pending` o `Running`, el job no se da por completado: queda en `Paused` para reanudarlo. `Failed` y `Cancelled` están documentados, pero ningún código los asigna.

- **Fallos de la base de datos:** un fallo pasajero (conexión perdida, PostgreSQL reiniciándose) no detiene a los workers: la partición vuelve a `Pending` sin gastar intento y se reintenta con espera creciente hasta que la base responde. Los errores de datos, en cambio, marcan la partición `Failed`.
- **Error inesperado:** si aun así los workers se detienen (5 errores inesperados seguidos, o un fallo al cerrar el job), se rescatan las particiones `Running` y el job pasa a `Paused`, en lugar de quedarse `Running` sin workers.
- **Reinicio del servicio:** al arrancar se reanudan los jobs que estaban en `Running`.

### DiscoveryPartitions.Status

```mermaid
stateDiagram-v2
  [*] --> Pending : generada o hija de otra
  Pending --> Running : un worker la adquiere
  Running --> Completed : respuesta completa
  Running --> Subdivided : truncada, se crean hijas
  Running --> PossiblyTruncated : truncada sin poder subdividir, con huecos o con elementos ilegibles
  Running --> Failed : error
  Running --> Pending : pausa
  Running --> Pending : rescate de huérfana
  Running --> Pending : fallo pasajero de la base de datos
  Failed --> Pending : Reintentar particiones fallidas
```

- **Tipos:** `Day` → `DayTime` (4 franjas de 6 h, luego bisección hasta 5 min) → `DayTimeModality` (franja mínima aún truncada: una hija por modalidad, último nivel). Los tipos antiguos `DayModality` y `DayModalityTime` se siguen procesando en los jobs que ya los tenían.
- **`PossiblyTruncated`:** la partición está truncada en el último nivel; o se subdividió, pero la subdivisión no garantiza cubrir todo (estudios sin hora, reparto por modalidad), y entonces las hijas se procesan igual; o la respuesta traía elementos que no se pudieron leer. El motivo queda en `LastError`.

Una partición que se queda en `Running` tras una caída vuelve a `Pending` al iniciar o reanudar el job (tras esperar hasta 60 s a los workers anteriores) y al acabar cada ronda de workers, con un máximo de 3 rondas. No consume `AttemptCount`.

### DiscoveryJobs.CaptureStatus

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> Running : Iniciar captura
  Running --> Paused : Pausar
  Paused --> Running : Reanudar
  Running --> Completed
  Running --> Failed
  Running --> Idle : Parar
  Paused --> Idle : Parar
```

### Migrations.PopulateStatus

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> Running : crear migración desde inventario
  Running --> Completed
  Running --> Failed
```

`PopulateSourceJobId` permite reanudarlo si el servicio se reinicia a mitad.

### NotificationOutbox.Status

```mermaid
stateDiagram-v2
  [*] --> Pending : evento notificado
  Pending --> Sent : enviado
  Pending --> Pending : falla, Attempts + 1
  Pending --> Failed : 5 intentos fallidos
```

## 4 · Flujo de datos entre tablas

Procesos → tablas

Qué proceso escribe en cada tabla, en el orden en que circulan los datos: del PACS de origen al inventario, del inventario a la migración, y de ahí a auditoría y notificaciones.

```mermaid
flowchart LR
  pDesc([Pantalla Descubrimiento]) --> tJobs[(DiscoveryJobs)]
  pDesc -- importación CSV --> tDS
  pEng([DiscoveryEngine]) --> tPart[(DiscoveryPartitions)]
  pEng --> tReq[(DiscoveryRequests)]
  pEng -- upsert --> tDS[(DiscoveredStudies)]
  pCap([Captura nivel 2]) --> tDI[(DiscoveredInstances)]
  tDS -. lee .-> pPop([Poblado de migración])
  tDI -. lee .-> pPop
  pPop --> tMS[(MigrationStudies)]
  pPop --> tMI[(MigrationInstances)]
  pDsv([Descubrimiento directoC-FIND · QIDO · CSV]) --> tMS
  pWrk([MigrationWorker]) --> tMS
  pVer([VerificationService]) --> tMS
  tMI -. lee .-> pVer
  pWrk --> tMig[(Migrations)]
  pVer --> tMig
  pWrk --> pBuf([AuditLogBuffer])
  pVer --> pBuf
  pBuf -- por lotes --> tAud[(AuditLogs)]
  pMnt([Mantenimiento diario]) -- purga INFO de más de 90 días --> tAud
  pMnt -- VACUUM y REINDEX de índices inflados --> tMI
  pMnt -- VACUUM y REINDEX de índices inflados --> tDI
  pBorr([Borrar migración o job]) --> pVac([VACUUM diferido])
  pVac -.-> tMS
  pVac -.-> tDS
  pWrk --> pNot([NotificationService])
  pVer --> pNot
  pEng --> pNot
  pNot --> tOut[(NotificationOutbox)]
  pDis([Repartidor de correo]) -- Sent o Failed --> tOut
  pLog([Inicio de sesión]) -- fallos y último acceso --> tUsr[(AppUsers)]
  pUsr([Pantalla Usuarios]) --> tUsr
```

La auditoría no se escribe en el momento: va a un búfer en memoria que se vuelca por lotes. Si el proceso muere de golpe, se pierden las entradas aún no volcadas.

El mantenimiento actúa sobre las siete tablas de más movimiento (`MigrationStudies`, `MigrationInstances`, `DiscoveredStudies`, `DiscoveredInstances`, `DiscoveryPartitions`, `DiscoveryRequests` y `AuditLogs`). El diagrama solo dibuja las flechas principales.

Esquema: Infrastructure/Migrations/AppDbContextModelSnapshot.cs, contrastado con la base local (pg_constraint, pg_indexes, pg_stat_user_tables). Estados y flujos: repositorios y servicios de Infrastructure y páginas de Web, leídos del directorio de trabajo del proyecto (commit 9814440, 10 oct 2026).