# Guía para activar HTTPS en DicomMigrator (MOVE)

> Guía paso a paso. Asume que **no sabes nada** de certificados y que quieres
> terminar con la aplicación funcionando en `https://`.
>
> Lee primero el **Paso 0**: ahí se decide todo lo demás.
>
> **Buena noticia:** no hay que tocar ni una línea de código. Todo es configuración.

---

## Índice

1. [Paso 0 — Decidir qué necesitas](#paso-0--decidir-qué-necesitas)
2. [Paso 1 — Elegir el tipo de certificado](#paso-1--elegir-el-tipo-de-certificado)
3. [Paso 2 — Obtener el certificado](#paso-2--obtener-el-certificado)
4. [Paso 3 — Instalarlo en Windows](#paso-3--instalar-el-certificado-en-windows)
5. [Paso 4 — Dar permisos al servicio](#paso-4--dar-permisos-al-servicio-el-paso-que-más-falla)
6. [Paso 5 — Configurar la aplicación](#paso-5--configurar-la-aplicación)
7. [Paso 6 — Abrir el firewall](#paso-6--abrir-el-firewall)
8. [Paso 7 — Reiniciar y comprobar](#paso-7--reiniciar-y-comprobar)
9. [Problemas frecuentes](#problemas-frecuentes)
10. [Cómo volver atrás](#cómo-volver-atrás)
11. [Anexo: fichero de configuración completo](#anexo-fichero-de-configuración-completo)

---

## Paso 0 — Decidir qué necesitas

Antes de tocar nada, responde a estas dos preguntas. **Determinan todo el resto.**

### Pregunta 1: ¿Desde dónde se usa la aplicación?

| Respuesta | Qué implica |
|---|---|
| **Solo desde el propio servidor** (abres el navegador en la máquina donde está instalada) | HTTPS es opcional: el tráfico no sale de la máquina. Puedes hacerlo igualmente, pero no es urgente |
| **Desde otros ordenadores de la red** | **HTTPS es obligatorio.** Ahora mismo viajan datos de paciente en claro por la red del hospital |

> ⚠️ Hoy la aplicación está configurada como `http://localhost:5200`. La palabra
> `localhost` significa que **solo acepta conexiones desde la propia máquina**.
> Si accedes desde otros equipos, ya lo habrás cambiado; si no, este es el momento
> de decidirlo (Paso 5).

### Pregunta 2: ¿El servidor está en un dominio Windows del hospital?

| Respuesta | Camino recomendado |
|---|---|
| **Sí, hay dominio y departamento de TI** | Pide un certificado a la **CA interna** (Paso 2, opción A). Es la opción correcta |
| **No, es una instalación aislada** | Certificado **autofirmado** (Paso 2, opción B). Funciona, pero cada navegador avisará |

Anota tus dos respuestas. Las necesitarás.

---

## Paso 1 — Elegir el tipo de certificado

Un certificado es un fichero que hace dos cosas: **cifra** la comunicación y
**demuestra** que el servidor es quien dice ser.

La diferencia entre los tipos no está en el cifrado —todos cifran igual de bien—
sino en **si el navegador se fía o no**.

| Tipo | Cifra | El navegador se fía | Coste |
|---|---|---|---|
| **CA interna** del hospital | ✅ | ✅ automático en equipos del dominio | Pedirlo a TI |
| **Autofirmado** | ✅ | ❌ avisa siempre (se puede instalar a mano en cada equipo) | Gratis, 2 minutos |
| **CA pública** (Let's Encrypt, DigiCert…) | ✅ | ✅ | Requiere nombre DNS público — raro en on-prem |

> 💡 **El aviso del navegador con un certificado autofirmado no significa que no
> haya cifrado.** El tráfico va cifrado igual. El aviso dice "no puedo verificar
> que este servidor sea quien dice". En una red interna controlada, es asumible.

### El nombre importa

El certificado se emite **para un nombre concreto**. Tienes que decidirlo ahora:

- Si accedes por nombre: `dicommigrator.hospital.local`
- Si accedes por IP: `192.168.1.50`

**Ese es el nombre que tendrás que escribir en el navegador.** Si el certificado
dice `dicommigrator.hospital.local` y entras por `https://192.168.1.50:5200`, el
navegador avisará aunque el certificado sea perfecto.

> 📌 Escribe aquí el nombre que vas a usar, lo necesitarás en varios pasos:
> `_______________________________`

---

## Paso 2 — Obtener el certificado

### Opción A — Certificado de la CA interna (recomendado si hay dominio)

Escribe a tu departamento de TI pidiendo esto:

> Necesito un certificado de servidor (SSL/TLS) para una aplicación web interna.
> - **Nombre (CN / SAN):** `dicommigrator.hospital.local` *(el que anotaste)*
> - **Servidor:** *(nombre del equipo)*
> - **Uso:** Server Authentication
> - **Formato:** PFX con clave privada, o emitido directamente en el almacén
>   `Equipo local → Personal` del servidor.

Te devolverán un fichero `.pfx` con su contraseña, o lo instalarán ellos
directamente. Si lo instalan ellos, **salta al Paso 4**.

### Opción B — Certificado autofirmado (instalación aislada)

Se genera en 2 minutos con PowerShell.

1. Abre **PowerShell como Administrador** en el servidor
   *(botón derecho sobre el icono → "Ejecutar como administrador")*

2. Ejecuta, cambiando el nombre por el tuyo:

```powershell
$cert = New-SelfSignedCertificate `
  -DnsName "dicommigrator.hospital.local", "localhost" `
  -CertStoreLocation "cert:\LocalMachine\My" `
  -FriendlyName "DicomMigrator HTTPS" `
  -NotAfter (Get-Date).AddYears(5) `
  -KeyExportPolicy Exportable `
  -KeySpec Signature

$cert.Thumbprint
```

3. **Apunta el "Thumbprint"** que aparece (una cadena larga de letras y números).

> ✅ Con esto el certificado **ya está instalado** en el almacén de Windows.
> Puedes saltar directamente al **Paso 4**.

> 💡 `-NotAfter (Get-Date).AddYears(5)` le da 5 años de validez. Sin ese parámetro
> caducaría en **1 año** y la aplicación dejaría de arrancar sin previo aviso.

---

## Paso 3 — Instalar el certificado en Windows

> ⏭️ **Sáltate este paso** si usaste la Opción B (autofirmado) o si TI lo instaló:
> el certificado ya está en su sitio.

Si te han dado un fichero `.pfx`:

1. Copia el `.pfx` al servidor.
2. **Doble clic** sobre él.
3. En el asistente:
   - Ubicación del almacén: **Equipo local** *(no "Usuario actual" — importante)*
   - Introduce la contraseña del PFX
   - Marca **"Marcar esta clave como exportable"**
   - Almacén de certificados: **Personal**
4. Finalizar.

### Comprobar que está

1. Pulsa `Win + R`, escribe `certlm.msc` y Enter.
2. Ve a **Personal → Certificados**.
3. Tu certificado debe aparecer ahí.
4. **Doble clic** sobre él → pestaña *General*. Abajo debe decir:
   *"Tiene una clave privada que corresponde a este certificado"*.

> ⚠️ Si **no** aparece ese mensaje, el certificado no sirve: falta la clave
> privada. Vuelve a instalar el `.pfx` completo.

---

## Paso 4 — Dar permisos al servicio (el paso que más falla)

**Este es el error número uno.** El certificado está bien, la configuración está
bien, y aun así el servicio no arranca: no puede *leer* la clave privada.

### Averiguar con qué cuenta corre el servicio

1. `Win + R` → `services.msc` → Enter
2. Busca el servicio **DicomMigrator**
3. Botón derecho → **Propiedades** → pestaña **Iniciar sesión**
4. Anota la cuenta:
   - `Sistema local` (LocalSystem) → **no necesitas hacer nada**, salta al Paso 5
   - `Servicio de red` (NetworkService) o una cuenta concreta → **continúa**

### Conceder el permiso

1. `Win + R` → `certlm.msc` → Enter
2. **Personal → Certificados**
3. **Botón derecho** sobre tu certificado → **Todas las tareas** → **Administrar
   claves privadas**
4. **Agregar…** → escribe el nombre de la cuenta del servicio → **Comprobar nombres**
   → **Aceptar**
5. Deja marcado solo **Lectura**
6. **Aceptar**

---

## Paso 5 — Configurar la aplicación

Aquí no se toca código. Solo un fichero de texto.

### El fichero

```
C:\DicomMigrator\appsettings.Production.json
```

> 📌 Este fichero se crea **a mano** en el servidor y no se sobrescribe al
> publicar una versión nueva. Si no existe, créalo.

### Antes de editar: haz copia

```powershell
Copy-Item C:\DicomMigrator\appsettings.Production.json `
          C:\DicomMigrator\appsettings.Production.json.bak
```

### La sección a cambiar

Busca la sección `"Kestrel"`. Ahora dirá algo parecido a:

```json
"Kestrel": {
  "Endpoints": {
    "Http": {
      "Url": "http://localhost:5200"
    }
  }
}
```

Sustitúyela por **una** de estas dos, según el caso.

#### Caso 1 — Solo HTTPS (recomendado)

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:5200",
      "Certificate": {
        "Subject": "dicommigrator.hospital.local",
        "Store": "My",
        "Location": "LocalMachine",
        "AllowInvalid": true
      }
    }
  }
}
```

Cambia `Subject` por el nombre de **tu** certificado.

> 🔧 `"AllowInvalid": true` es necesario **si el certificado es autofirmado**.
> Si es de una CA interna o pública, ponlo a `false`.

#### Caso 2 — HTTP y HTTPS a la vez (transición)

Útil mientras avisas a los usuarios: quien entre por HTTP será **redirigido
automáticamente** a HTTPS.

```json
"Kestrel": {
  "Endpoints": {
    "Http": {
      "Url": "http://0.0.0.0:5201"
    },
    "Https": {
      "Url": "https://0.0.0.0:5200",
      "Certificate": {
        "Subject": "dicommigrator.hospital.local",
        "Store": "My",
        "Location": "LocalMachine",
        "AllowInvalid": true
      }
    }
  }
}
```

> La aplicación detecta sola que hay un endpoint HTTPS y **activa la redirección
> automática**. No hay que configurar nada más.

### ¿`localhost` o `0.0.0.0`?

| Valor | Significado |
|---|---|
| `localhost` | Solo se puede acceder **desde el propio servidor** |
| `0.0.0.0` | Se puede acceder **desde cualquier equipo de la red** |

Si respondiste "desde otros ordenadores" en el Paso 0, usa `0.0.0.0`.

### Alternativa: certificado desde fichero PFX

Si prefieres no usar el almacén de Windows (te saltas el Paso 4, pero la
contraseña queda escrita en un fichero):

```json
"Https": {
  "Url": "https://0.0.0.0:5200",
  "Certificate": {
    "Path": "C:\\DicomMigrator\\cert.pfx",
    "Password": "la-contraseña-del-pfx"
  }
}
```

> ⚠️ Fíjate en las **barras dobles** `\\` en la ruta. Es obligatorio en JSON.
> Con una sola barra el fichero no se lee y el servicio no arranca.

### Comprobar que el JSON es válido

Un error de sintaxis (una coma de más, una comilla suelta) impide arrancar.
Compruébalo antes de reiniciar:

```powershell
Get-Content C:\DicomMigrator\appsettings.Production.json -Raw | ConvertFrom-Json
```

Si no dice nada, el fichero es correcto. Si da error, revisa comas y llaves.

---

## Paso 6 — Abrir el firewall

Solo si vas a acceder **desde otros equipos**.

En PowerShell **como Administrador**:

```powershell
New-NetFirewallRule -DisplayName "DicomMigrator HTTPS" `
  -Direction Inbound -Protocol TCP -LocalPort 5200 -Action Allow
```

---

## Paso 7 — Reiniciar y comprobar

### Reiniciar el servicio

```powershell
Restart-Service DicomMigrator
```

O desde `services.msc`: botón derecho sobre **DicomMigrator** → **Reiniciar**.

### Comprobar el log

Los logs están en `C:\DicomMigrator\logs\`. Abre el más reciente y busca una
línea parecida a:

```
Now listening on: https://0.0.0.0:5200
```

Si dice `https://`, **ha funcionado**.

### Comprobar desde el navegador

Entra por el nombre exacto del certificado:

```
https://dicommigrator.hospital.local:5200
```

- **Candado cerrado** → perfecto, todo correcto.
- **Aviso de seguridad** → normal con certificado autofirmado. Pulsa
  "Avanzado" → "Continuar". Ver *Problemas frecuentes* para quitarlo.

---

## Problemas frecuentes

### El servicio no arranca

Mira el log en `C:\DicomMigrator\logs\` y busca el mensaje:

| Mensaje | Causa | Solución |
|---|---|---|
| `The certificate ... was not found` | El `Subject` no coincide con el certificado | Revisa el nombre exacto en `certlm.msc` |
| `Keyset does not exist` / `Acceso denegado` | El servicio no puede leer la clave privada | **Paso 4** |
| `No such file or directory` (con PFX) | Ruta mal escrita | Revisa las barras dobles `\\` |
| `Failed to bind to address` | El puerto ya está en uso | Otro programa usa el 5200; cambia de puerto |
| Error de JSON al arrancar | Sintaxis del fichero | Valida con el comando del Paso 5 |

### "No se puede acceder a este sitio" desde otro equipo

Comprueba en orden:

1. ¿Pusiste `0.0.0.0` y no `localhost`? *(Paso 5)*
2. ¿Abriste el firewall? *(Paso 6)*
3. ¿El nombre resuelve? Prueba `ping dicommigrator.hospital.local`

### El navegador avisa de certificado no seguro

Es lo esperado con autofirmado. Dos opciones:

**A) Convivir con ello** — pulsar "Avanzado → Continuar" cada vez.

**B) Quitar el aviso** instalando el certificado como *confiable* en cada equipo
cliente:

1. En el servidor, exporta el certificado **sin clave privada**:

```powershell
$c = Get-ChildItem cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq "DicomMigrator HTTPS" }
Export-Certificate -Cert $c -FilePath C:\DicomMigrator\dicommigrator-public.cer
```

2. Copia ese `.cer` a cada equipo cliente.
3. En el cliente: doble clic → **Instalar certificado** → **Equipo local** →
   *Colocar todos los certificados en el siguiente almacén* → **Entidades de
   certificación raíz de confianza**.

> En un dominio, TI puede distribuirlo a todos los equipos por directiva de grupo
> (GPO) de una vez.

### Volví a HTTP y ahora el navegador no me deja entrar

Esto tiene explicación y **no es un fallo**. La aplicación envía una cabecera
(HSTS) que le dice al navegador *"para este servidor, usa solo HTTPS durante los
próximos 30 días"*. Si vuelves a HTTP, el navegador se niega.

**Solución en Chrome / Edge:**

1. Ve a `chrome://net-internals/#hsts` *(o `edge://net-internals/#hsts`)*
2. En **"Delete domain security policies"**, escribe el nombre del servidor
3. Pulsa **Delete**

> 📌 **Por eso conviene decidir el esquema antes de dar acceso a los usuarios**:
> ir y volver entre HTTP y HTTPS obliga a limpiar esto en cada navegador.

### El certificado ha caducado

La aplicación dejará de arrancar de golpe. Repite el **Paso 2** para obtener uno
nuevo y el **Paso 4** para los permisos. La configuración del Paso 5 no cambia si
el nombre es el mismo.

> 💡 Ponte un recordatorio en el calendario un mes antes de la fecha de caducidad.
> Para verla: `certlm.msc` → Personal → Certificados → columna *Fecha de expiración*.

---

## Cómo volver atrás

Si algo sale mal y necesitas dejarlo como estaba:

```powershell
Copy-Item C:\DicomMigrator\appsettings.Production.json.bak `
          C:\DicomMigrator\appsettings.Production.json -Force
Restart-Service DicomMigrator
```

Recuerda que quizá tengas que limpiar el HSTS del navegador (ver arriba).

---

## Anexo: fichero de configuración completo

Ejemplo de `C:\DicomMigrator\appsettings.Production.json` con HTTPS activado.
Los valores marcados con ⬅️ son los que debes adaptar.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=dicommigrator;Username=dicom_app_migrator;Password=TU_PASSWORD"
  },

  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5200",
        "Certificate": {
          "Subject": "dicommigrator.hospital.local",
          "Store": "My",
          "Location": "LocalMachine",
          "AllowInvalid": true
        }
      }
    }
  },

  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning"
    }
  },

  "Maintenance": {
    "AuditLogRetentionDays": 90,
    "PurgeIntervalHours": 24,
    "RunPurgeOnStartup": true
  },

  "AllowedHosts": "*"
}
```

⬅️ A adaptar:
- `Password` de la base de datos
- `Subject` — el nombre de tu certificado
- `AllowInvalid` — `true` si es autofirmado, `false` si es de una CA

---

## Resumen en 6 líneas

1. Consigue un certificado (CA interna, o autofirmado con PowerShell).
2. Instálalo en **Equipo local → Personal**.
3. Da permiso de **lectura de clave privada** a la cuenta del servicio.
4. Cambia la sección `Kestrel` de `appsettings.Production.json` a `Https`.
5. Abre el puerto en el firewall si accedes desde la red.
6. `Restart-Service DicomMigrator` y comprueba el log.

---

## Nota final: esto no es lo mismo que el TLS de DICOM

Esta guía cifra **la interfaz web** — lo que ves en el navegador.

Es **independiente** del cifrado del tráfico DICOM contra los PACS, que se
configura por nodo en la propia aplicación (`UseTls` en DIMSE, `ValidateTls` en
DICOMweb).

Son dos capas distintas: puedes tener una activada y la otra no. Para una
instalación completa en producción, conviene revisar ambas.
