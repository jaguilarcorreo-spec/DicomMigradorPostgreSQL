# DicomMigrator — Tags DICOM: consultados vs. almacenados

> **Actualizado a la v207**, que pobló las seis columnas que existían en la tabla
> pero nunca se pedían al PACS.

Comparativa entre los tags que **consulta** DicomMigrator (vía DIMSE / DICOMweb),
los que **almacena** en base de datos, y los tags de la estructura de referencia
de un objeto DICOM completo.

> **Nota:** desde la v207, DIMSE y QIDO piden **exactamente el mismo conjunto** de
> 15 tags a nivel estudio. Antes el QIDO no enviaba `includefield` y dependía del
> conjunto por defecto de cada servidor.

---

## Nivel ESTUDIO

| Tag | Nombre | Consulta | Almacena | En la lista de referencia |
|---|---|---|---|---|
| (0020,000D) | StudyInstanceUID | ✅ | ✅ | ✅ |
| (0008,0020) | StudyDate | ✅ | ✅ | ✅ |
| (0008,0030) | StudyTime | ✅ **v207** | ✅ | ✅ |
| (0008,0050) | AccessionNumber | ✅ | ✅ | ✅ |
| (0008,0054) | RetrieveAETitle | ✅ **v207** | ✅ | ❌ |
| (0008,0061) | ModalitiesInStudy | ✅ | ✅ | ✅ |
| (0008,0080) | InstitutionName | ✅ **v207** | ✅ | ✅ |
| (0008,1030) | StudyDescription | ✅ | ✅ | ✅ |
| (0020,1206) | NumberOfStudyRelatedSeries | ✅ | ✅ | ❌ |
| (0020,1208) | NumberOfStudyRelatedInstances | ✅ | ✅ | ❌ |
| (0020,0010) | StudyID | ❌ | ❌ | ✅ |
| (0008,0090) | ReferringPhysicianName | ❌ | ❌ | ✅ |
| (0008,1010) | StationName | ❌ | ❌ | ✅ |
| (0008,1070) | OperatorsName | ❌ | ❌ | ✅ |
| (0028,0008) | NumberOfFrames | ❌ | ❌ | ✅ (es de instancia) |
| (0010,1010) | PatientAge | ❌ | ❌ | ✅ (Patient Study Module) |
| (0010,1030) | PatientWeight | ❌ | ❌ | ✅ (Patient Study Module) |

---

## Nivel PACIENTE — cobertura completa desde v207

| Tag | Nombre | Consulta | Almacena | En la lista de referencia |
|---|---|---|---|---|
| (0010,0020) | PatientID | ✅ | ✅ | ✅ |
| (0010,0010) | PatientName | ✅ | ✅ | ✅ |
| (0010,0021) | IssuerOfPatientID | ✅ **v207** | ✅ | ❌ |
| (0010,0030) | PatientBirthDate | ✅ **v207** | ✅ | ✅ |
| (0010,0040) | PatientSex | ✅ **v207** | ✅ | ✅ |

---

## Nivel SERIE

| Tag | Nombre | Consulta | Almacena | En la lista de referencia |
|---|---|---|---|---|
| (0020,000E) | SeriesInstanceUID | ✅ solo al **enumerar** (Nivel 2) | ✅ | ✅ |
| (0008,0021) | SeriesDate | ❌ | ❌ | ✅ |
| (0008,0031) | SeriesTime | ❌ | ❌ | ✅ |
| (0008,103E) | SeriesDescription | ❌ | ❌ | ✅ |
| (0020,0011) | SeriesNumber | ❌ | ❌ | ✅ |
| (0008,0060) | Modality | ❌ | ❌ | ✅ |
| (0018,5100) | PatientPosition | ❌ | ❌ | ✅ |
| (0040,1001) | RequestedProcedureID | ❌ | ❌ | ✅ |
| (0040,0009) | ScheduledProcedureStepID | ❌ | ❌ | ✅ |
| (0040,0244) | PerformedProcedureStepStartDate | ❌ | ❌ | ✅ |
| (0040,0245) | PerformedProcedureStepStartTime | ❌ | ❌ | ✅ |

---

## Nivel INSTANCIA (Imagen)

| Tag | Nombre | Consulta | Almacena | En la lista de referencia |
|---|---|---|---|---|
| (0008,0018) | SOPInstanceUID | ✅ solo al **enumerar** (Nivel 2) | ✅ | ✅ |
| (0008,0016) | SOPClassUID | ❌ | ❌ | ✅ |
| (0020,0013) | InstanceNumber | ❌ | ❌ | ✅ |
| (0008,0023) | ContentDate | ❌ | ❌ | ✅ |
| (0008,0033) | ContentTime | ❌ | ❌ | ✅ |

---

## Módulo de píxel y misceláneos

| Tag(s) | Consulta | Almacena |
|---|---|---|
| (7FE0,0010) PixelData | ❌ nunca | ❌ nunca |
| (0028,0010) Rows, (0028,0011) Columns | ❌ nunca | ❌ nunca |
| (0028,0002) SamplesPerPixel, (0028,0004) PhotometricInterpretation | ❌ nunca | ❌ nunca |
| (0028,0006) PlanarConfiguration | ❌ nunca | ❌ nunca |
| (0028,0100) BitsAllocated, (0028,0101) BitsStored, (0028,0102) HighBit | ❌ nunca | ❌ nunca |
| (0028,0103) PixelRepresentation | ❌ nunca | ❌ nunca |
| (0008,0005) SpecificCharacterSet | ❌ explícito (lo gestiona fo-dicom) | ❌ |

---

## Qué cambió con la v207

| | Antes (v206) | Ahora (v207) |
|---|---|---|
| Tags de estudio consultados | 9 | **15** |
| Columnas huérfanas (en tabla, nunca pobladas) | 6 | **0** |
| Cobertura del bloque Paciente | 2 de 4 | **4 de 4** |
| `includefield` en QIDO | no se enviaba | explícito, 15 tags |

### Conjunto `includefield` que se envía ahora en QIDO

```
0020000D,00080020,00080030,00080050,00080054,00080061,00080080,00081030,
00100010,00100020,00100021,00100030,00100040,00201206,00201208
```

---

## Conclusiones

### 1. DIMSE y QIDO están alineados

Ambos protocolos piden el mismo conjunto de 15 tags en el descubrimiento. Un matiz:
en la consulta de **verificación** por QIDO se piden únicamente
`includefield=0020000D,00201206,00201208` (UID + conteos), porque la verificación de
Nivel 1 solo necesita conteos.

### 2. Ya no hay columnas huérfanas, pero puede haber campos vacíos

Las seis columnas se piden ahora al PACS, pero **`RetrieveAETitle` (0008,0054) e
`IssuerOfPatientID` (0010,0021) probablemente lleguen vacías** en Orthanc: son
atributos Tipo 3 y pocos PACS los sirven a nivel estudio. La distinción importa:
antes estaban vacías *por diseño de la aplicación*; ahora, si lo están, es *por el
origen*.

Detalle de implementación: el motor normaliza `""` → `null` antes de guardar. Si se
almacenara cadena vacía, el `??=` del upsert la trataría como "ya rellena" y nunca
podría completarse en un redescubrimiento posterior.

### 3. El migrador sigue sin ver píxeles ni tags de instancia

Más allá del `SOPInstanceUID`, no abre los objetos DICOM. El traslado se realiza por
**C-MOVE** (PACS → PACS) sin que la aplicación inspeccione el contenido.

Por eso la **verificación Nivel 2 compara identidades (UIDs), no contenido**. Una
verificación de contenido real (hashes de píxel, comparación de datasets) exigiría
recuperar las instancias vía C-GET/WADO: cambio de naturaleza y de coste muy
distintos.

---

## Resumen ejecutivo

| Ámbito | Alcance real de DicomMigrator |
|---|---|
| **Metadatos de estudio** | Se consultan y almacenan (10 tags) para catalogar y filtrar |
| **Metadatos de paciente** | Los 5 del bloque de identificación |
| **Metadatos de serie** | Solo SeriesInstanceUID (al enumerar) |
| **Metadatos de instancia** | Solo SOPInstanceUID (al enumerar) |
| **Datos de píxel** | Nunca se acceden |

**En una frase:** el migrador maneja metadatos de estudio y paciente para catalogar y
filtrar, y solo UIDs (serie e instancia) para verificar identidad. Todo lo demás es
opaco para él.
