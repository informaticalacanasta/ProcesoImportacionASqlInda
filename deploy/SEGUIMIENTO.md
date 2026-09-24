# Seguimiento del importador

El importador sigue leyendo XML, validando, insertando el ticket y archivando como hasta ahora. Este documento describe el seguimiento añadido: qué se importó, cuándo, con qué resultado, qué falló, qué se reintentará y si el proceso sigue vivo.

## Diseño

Hay dos planos distintos.

- El estado actual sigue en `TICKET_RECEPCION` (`ESTADO`, `ESTADO_ARCHIVO`, `MENSAJE_ERROR`, `NUMERO_INTENTO`). Un reintento sigue reutilizando la recepción y limpiando el mensaje de error anterior.
- El historial nuevo no se pisa. Cada intento real es una fila de `IMPORTADOR_INTENTO`. Cada transición es una fila de `IMPORTADOR_EVENTO`. Si un reintento acaba bien, el intento fallido anterior permanece.

Un intento solo queda como `IMPORTADO` o `IMPORTADO_CON_ADVERTENCIAS` después de confirmar la transacción del ticket. Esa escritura va fuera de la transacción. SQL Server puede dejar una transacción no confirmable si el seguimiento falla dentro de ella (`XACT_ABORT`, pérdida de conexión u otros errores graves). Un `TRY/CATCH` que devuelve 0 no hace confirmable ese ticket, así que el importador no enlista el seguimiento en la transacción de negocio. Si el proceso muere entre el `COMMIT` y el registro, el intento sigue `EN_CURSO` y la reconciliación lo cierra solo cuando `TICKET_RECEPCION` demuestra el resultado de ese mismo número de intento. El fallo de negocio se registra después, fuera de la transacción revertida. Si además falla ese registro, se conserva el error original y, si el disco lo permite, el evento queda en la bandeja local.

Una pérdida de conexión durante `COMMIT` no se da por buena ni por mala a ciegas. Se vuelve a leer `TICKET_RECEPCION` por `ID_RECEPCION`. Si el estado ya es terminal, se usa ese estado. Si no se puede leer, el resultado es `INCIERTO` y no se reescribe la recepción: el XML sigue en entrada y la protección de duplicados evita una segunda inserción.

El descubrimiento repetido de un archivo no crea un intento. El intento empieza cuando de verdad se va a importar. Recuperar un archivado pendiente tampoco es un intento nuevo.

`PrepareRetryAsync`, el paralelismo (`MaxConcurrency` 20), la cola (100), el escaneo de respaldo (10 segundos), las esperas crecientes de SQL y las reglas de duplicado, conflicto y archivado no cambian.

## Tablas

No hay clave foránea hacia `TICKET_RECEPCION`. Un intento de conexión puede existir antes de la recepción.

### IMPORTADOR_EJECUCION

Una fila por arranque. `ID_EJECUCION` se genera en el proceso antes de abrir SQL.

| Campo | Significado |
| --- | --- |
| NOMBRE_APLICACION | `DbInda.Worker` |
| NOMBRE_INSTANCIA | `Tracking:InstanceName` o `equipo:pid` |
| EQUIPO, ID_PROCESO, VERSION_APLICACION | Máquina, proceso y versión del ensamblado |
| FECHA_INICIO_UTC, FECHA_SENAL_UTC | Arranque y última señal, en UTC |
| FECHA_PARADA_UTC, ESTADO, MOTIVO_FINALIZACION | Solo si el proceso termina de forma ordenada. `ACTIVA` o `DETENIDA` |

Una parada abrupta no puede escribir esta fila. Se ve porque `ESTADO` sigue `ACTIVA` y `FECHA_SENAL_UTC` envejece. La vista `VW_IMPORTADOR_EJECUCION_SIN_SENAL` usa 3 minutos, el mismo valor por defecto que `Tracking:StaleSignalMinutes`. El proceso nuevo no cierra la fila de la ejecución anterior.

### IMPORTADOR_INTENTO

Una fila por intento real. El estado de esa fila es el estado de ese intento, no el de los anteriores.

| Campo | Significado |
| --- | --- |
| ID_INTENTO | GUID creado en el proceso |
| ID_EJECUCION | Ejecución que lo hizo |
| ID_RECEPCION, ID_TICKET | Nulos si SQL cayó antes de crear la recepción |
| CORRELACION_ORIGEN | SHA-256 de la ruta normalizada. No es un id de recepción |
| HASH_SHA256 | Hash del XML cuando ya se pudo calcular |
| TIENDA, TPV | Del nombre o del ticket, cuando se conocen |
| NUMERO_INTENTO | El de `TICKET_RECEPCION` cuando el intento está ligado a una recepción. Nulo en un intento de conexión |
| TIPO_INTENTO | `CONEXION` o `RECEPCION` |
| FECHA_INICIO_UTC, FECHA_FIN_UTC, DURACION_MS | UTC. La duración usa un reloj monotónico, no el reloj de pared |
| FASE | `CONEXION`, `RECEPCION`, `INSERCION`, `ARCHIVO`, `RECONCILIACION`, `SERVICIO` |
| RESULTADO | Ver la lista de abajo |
| CATEGORIA_ERROR, CODIGO_ERROR, MENSAJE | Resumen recortado a 40, 64 y 1000 caracteres |
| ID_DETALLE_TECNICO | `ID_EVENTO` del detalle. No es el XML |
| FECHA_PROXIMO_REINTENTO_UTC | Solo si se programó un reintento |
| REGISTROS_CONFIRMADOS | Ticket y filas hijas contadas en memoria, sin otra consulta. Nulo si no hubo inserción nueva |

Resultados: `EN_CURSO`, `IMPORTADO`, `IMPORTADO_CON_ADVERTENCIAS`, `DUPLICADO`, `CONFLICTO`, `ERROR_XML`, `ERROR_PERMANENTE`, `ERROR_SQL`, `SQL_NO_DISPONIBLE`, `INCIERTO`.

Solo `IMPORTADO` e `IMPORTADO_CON_ADVERTENCIAS` son tickets nuevos. Duplicado y conflicto no lo son. `INCIERTO` no es ni éxito ni fallo demostrado.

Textos que no caben se cortan y acaban en `…`. No se guarda el XML ni la cadena de conexión.

### IMPORTADOR_EVENTO

Historial de transiciones. La clave primaria es `ID_EVENTO`: reenviar el mismo evento no duplica la fila.

Tipos: `INTENTO_INICIADO`, `ERROR`, `ERROR_REINTENTO_PROGRAMADO`, `IMPORTACION_CONFIRMADA`, `DUPLICADO`, `CONFLICTO`, `ARCHIVADO_INICIADO`, `ARCHIVADO_COMPLETADO`, `ARCHIVADO_FALLIDO`, `RECONCILIACION`, `SQL_INACCESIBLE`, `SQL_RECUPERADO`, `SERVICIO_INICIADO`, `SERVICIO_PARADO`.

`SECUENCIA` ordena los hechos de una ejecución. `DETALLE` es JSON o texto corto, máximo 2000 caracteres.

Un fallo de archivado es un evento `ARCHIVADO_FALLIDO`. No cambia el resultado de la importación SQL.

## Contadores y solapes

En cada señal de salud:

- Pendientes en disco: XML directos de la carpeta de entrada. La antigüedad sale de la primera observación del proceso, guardada en `observaciones.json` en cada señal de salud. Un corte anterior a esa señal vuelve a tomar la siguiente observación como inicio. No se usa la fecha de modificación del archivo.
- Cola: elementos del canal. Ya están contados en el vuelo y también están en disco.
- En vuelo: reclamados por el pipeline (estabilización, cola y procesamiento). No sumar cola + vuelo.
- Esperando reintento: rutas con backoff de SQL, todavía en disco.
- Archivados pendientes: recepciones `ARCHIVANDO` cuya `FECHA_ULTIMO_INTENTO` local es más antigua que el umbral. Esa fecha es la del intento de importación, no un cronómetro del movimiento.

## Cuando SQL no está

La bandeja local es un directorio de ficheros JSON, no un servidor de colas. Por defecto, `Paths:Logs/seguimiento`. Los eventos nuevos se escriben en `seguimiento/pendientes`, un fichero por elemento: `{ejecucion|intento|evento}-{guid}.json`, en un temporal y renombrado después de `Flush(true)`. En la raíz del mismo directorio viven archivos de estado que no son eventos: `salud.json`, `observaciones.json`, `alertas-estado.json` y `alertas.log`. La lectura, el tamaño, el límite y la cuarentena solo tocan ficheros de evento.

Un evento escrito por la versión anterior, con ese mismo nombre y en la raíz, se reenvía una vez y se borra. No se copia antes, así que no se duplica. `corruptos` no se vuelve a leer: no se restauran solos ni los eventos ni los archivos de estado.

Al volver SQL, se reenvían en orden de secuencia. El fichero local se borra solo después de que SQL acepte la operación. Repetirla no duplica: la ejecución se actualiza si su secuencia es mayor; el intento solo si la secuencia es mayor y el resultado guardado sigue `EN_CURSO`, coincide, o es `INCIERTO` y llega un resultado demostrado. El evento se inserta solo si su GUID no existe.

Un JSON ilegible, un tipo desconocido, un identificador vacío o un sobre sin la carga que declara se mueven a `seguimiento/corruptos` y el resto sigue. No se borra un evento hasta que SQL lo acepta. Si se alcanza `OutboxMaxFiles` o `OutboxMaxBytes`, no se aceptan eventos nuevos y no se borran los pendientes. Esos límites no cuentan los archivos de estado. Si el disco está lleno, el aviso sale por el log del proceso una vez, sin reintentar en bucle. En ese caso la persistencia local no es ilimitada y puede perderse el evento que no cupo. La importación de tickets continúa.

`salud.json`, en el mismo directorio, resume la última señal. No demuestra que el proceso siga vivo después de morir: lo demuestra la ausencia de actualizaciones.

## Logs

`Paths:Logs/worker-YYYYMMDD.log` es JSON, una línea por evento, con `ts` en UTC y sufijo `Z`. Si pasa de `Logging:MaxFileBytes` (20 MB), rota a `worker-YYYYMMDD-NNN.log`. La retención de estos ficheros sigue siendo `Logging:RetainedDays` (31). Un fallo de escritura o de borrado no tumba la importación; se reintenta al cabo de un minuto.

El descubrimiento, el encolado y el fin de cada escaneo rutinario van a Debug. El resultado de cada importación y las incidencias siguen en Information, Warning o Error. Donde antes solo se decía que el XML no era procesable, el log incluye `ImportResult.Errors`. Una `SqlException` añade número, estado, clase, procedimiento y línea. No se escriben contraseñas, el XML completo ni la cadena de conexión.

`stdout` del servicio sigue en `/opt/TicketsTPV/logs/worker.log` según la unidad systemd. Ese fichero no sustituye al log diario.

## Salud y alertas

Cada `Tracking:HeartbeatSeconds` (60) se actualiza la señal y se escribe un log de salud. Proceso vivo y procesamiento sano son campos distintos.

| Estado de procesamiento | Cuándo |
| --- | --- |
| ESPERANDO_DATOS | SQL y entrada accesibles, sin XML pendiente |
| SANO | Hay trabajo y no hay bloqueo |
| DEGRADADO | SQL acaba de caer, hay reintentos, bandeja local o archivados pendientes |
| BLOQUEADO | SQL caído el tiempo configurado, entrada inaccesible, pendientes sin progreso o bandeja llena |

Reglas, todas con clave estable, apertura, recordatorio y aviso de recuperación:

| Clave | Condición por defecto |
| --- | --- |
| sql-inaccesible | SQL inaccesible 5 minutos. Una sola alerta, no una por archivo |
| pendientes-sin-progreso | Hay XML en entrada y no hay progreso durante 10 minutos |
| pendientes-antiguos | La primera observación supera 30 minutos |
| seguimiento-sin-enviar | El evento local más antiguo supera 10 minutos |
| seguimiento-lleno | La bandeja llegó al límite o el disco no admite más |
| archivado-atascado | Recepciones en `ARCHIVANDO` por encima de 15 minutos |
| conflictos-nuevos | Conflictos con `FECHA_PROCESADO` posterior al último ciclo. No reavisa el histórico |
| silencio-{tienda}-{tpv} | Solo si esa tienda está en `ExpectedSources` |

`ExpectedSources` está vacío. Sin horario, días activos, gracia y cadencia no hay alerta por tienda. Una tienda que nunca envió archivos no se puede detectar de otra forma.

El estado de alertas está en `alertas-estado.json`. Cada incidencia guarda, por canal, el identificador del aviso, si está pendiente, enviado o abandonado, y el próximo intento. El canal local y el webhook no comparten ese estado: que el log local se escriba no marca el webhook como enviado. Un reinicio conserva los pendientes. El reintento usa el mismo identificador para que el receptor pueda descartar un duplicado si aceptó la petición y se perdió la respuesta. La espera es `Tracking:Alerts:RetrySeconds` (60) multiplicada por el número de intento, con tope `ReminderMinutes`, y como máximo `MaxAttempts` (5). Al llegar al máximo el aviso se abandona y queda una línea en el log, sin la URL. Un canal desactivado no genera pendientes. Si la incidencia se reabre mientras queda una recuperación sin entregar, empieza un aviso nuevo y no se queda bloqueada.

El canal local es `alertas.log` más el log del proceso. El webhook está desactivado. Si se activa, la URL sale de configuración o de `Tracking__Alerts__WebhookUrl` en el entorno privado, nunca del repositorio ni de los logs.

La muerte del proceso no la detecta el propio proceso. `deploy/comprobar-salud.sh` consulta en solo lectura la señal y termina con código 2 si supera el umbral. `deploy/ticketstpv-salud.service.example` y `deploy/ticketstpv-salud.timer.example` muestran cómo engancharlo a systemd. No están instalados.

## Consultas

`sql/consultas/operacion.sql` y las vistas de `sql/06_Vistas_Seguimiento.sql`.

Hoy es el día civil de `Romance Standard Time` (Europe/Madrid). Las columnas nuevas están en UTC y la vista las convierte de forma explícita. `FECHA_PROCESADO` se lee tal cual: es hora local de la aplicación y no es el instante exacto del `COMMIT`. `FECHA_EXPEDICION` es la fecha de la factura.

Hasta el primer intento posterior al despliegue, `ULTIMA_IMPORTACION_UTC` puede ser nula aunque haya años de tickets. Para ese histórico se usa `ULTIMA_FECHA_PROCESADO_LOCAL`, filtrando `PROCESADO` y `PROCESADO_CON_ADVERTENCIAS`. No vale `MAX(FECHA_PROCESADO)` sin ese filtro: duplicados y conflictos también lo rellenan.

Para las alertas de silencio, si aún no hay intento UTC, `FECHA_PROCESADO` se interpreta como hora local de `Tracking:BusinessTimeZone`. No se reescribe la columna.

Los eventos pendientes de envío no tienen consulta SQL. Están en el directorio de seguimiento y en `salud.json` (`OutboxPending`, `OldestOutboxUtc`).

## Investigar una recepción

1. `TICKET_RECEPCION`: estado actual, archivo, mensaje vigente y ticket.
2. `IMPORTADOR_INTENTO` por `ID_RECEPCION`: cada intento, incluido el que falló antes de acertar.
3. `IMPORTADOR_EVENTO` por `ID_RECEPCION` o `ID_INTENTO`: transiciones y el detalle técnico.
4. Si `ID_RECEPCION` es nulo, buscar por `CORRELACION_ORIGEN` o por la ruta. Eso ocurre cuando SQL cayó antes de crear la recepción.
5. `ESTADO_ARCHIVO = ARCHIVANDO` o `ERROR_ARCHIVO` con `ESTADO` ya terminal significa que el ticket quedó y falló el movimiento del XML.

## Caída de SQL

Los XML permanecen en entrada. El backoff sigue siendo el de `Retry` (5 segundos, multiplicador 2, máximo 300). Los intentos de conexión quedan en la bandeja local y, al recuperarse SQL, pasan a `IMPORTADOR_INTENTO` con `TIPO_INTENTO = CONEXION` y sin número de intento. El evento `SQL_INACCESIBLE` marca el corte general; `SQL_RECUPERADO`, la vuelta. No se genera una alerta por archivo.

Tras un reinicio, o cuando SQL vuelve después de un arranque sin conexión, se reconcilian los intentos `EN_CURSO` de otra ejecución que ya tiene `FECHA_PARADA_UTC` o cuya `FECHA_SENAL_UTC` es más antigua que `StaleSignalMinutes`. No se tocan los de la ejecución actual ni los de una ejecución que sigue señalando. La actualización solo modifica la fila si continúa `EN_CURSO`; si otro proceso ya la cerró, no se emite reconciliación. La secuencia de cada proceso no sirve como versión global: un contador que vuelve a empezar no impide el cierre.

El resultado sale de `TICKET_RECEPCION` solo si `NUMERO_INTENTO` coincide con el del intento. Un reintento posterior que deje la recepción en `PROCESADO` no convierte en éxito el intento anterior. Si no hay esa prueba, el resultado es `INCIERTO`. No se escribe hora de fin. Un evento atrasado con resultado `EN_CURSO` no reabre un cierre. Un `INCIERTO` sí puede sustituirse después por un resultado demostrado de secuencia mayor, por ejemplo el que llegue desde la bandeja.

La última importación correcta y el reloj de progreso viven en la ejecución actual. No sobreviven a un reinicio. Lo que sí persiste es la primera vez que se vio cada XML (`observaciones.json`), y ese dato alimenta la alerta de antigüedad, no la de falta de progreso. El bloqueo por falta de progreso empieza cuando aparece trabajo pendiente; un archivo que llega tras un periodo de inactividad dispone del plazo completo (`PendingWithoutProgressMinutes`). Duplicados, conflictos y errores definitivos cuentan como trabajo atendido, no como importación nueva. Un commit revertido o un resultado `INCIERTO` no actualizan la última importación. Dos actualizaciones concurrentes no mueven esa hora hacia atrás.

La caché en memoria guarda como máximo 256 intentos. Los ya terminados se sueltan. Los que esperan reintento se conservan unos dos minutos y, si se expulsan, la hora del próximo reintento se escribe en SQL por `ID_INTENTO`. La persistencia no depende de esa caché.

## Retención

Los logs técnicos se borran a los 31 días (`Logging:RetainedDays`). El historial SQL no se borra salvo que `Tracking:Retention:Enabled` sea true y `HistoryDays` sea al menos 1. Ese número lo elige la operación; no es un plazo legal. La limpieza va por lotes de `BatchSize` y no borra intentos `EN_CURSO` o `INCIERTO`, ejecuciones aún activas, tickets, XML ni ficheros de la bandeja local. Hace falta el `DELETE` comentado al final de `sql/07_GrantTracking.sql`. Sin ese permiso, la limpieza registra el error y la importación sigue.

## Despliegue

1. Parar `ticketstpv`.
2. Copia de seguridad de la base y de `/home/tpv_recepcion`, incluido `inbox/.organizacion` si existe.
3. En SSMS o `sqlcmd`, contra `TicketsTPV` y como administrador, ejecutar en este orden:
   - `sql/05_CreateTracking.sql`
   - `sql/06_Vistas_Seguimiento.sql`
   - `sql/07_GrantTracking.sql`
4. No ejecutar `sql/opcional/AuditoriaSqlServer.sql` salvo una decisión aparte.
5. Publicar:

```powershell
dotnet publish src/DbInda.Worker/DbInda.Worker.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
```

6. Subir el publicado con el servicio parado. Conservar `/etc/ticketstpv/ticketstpv.env` y no sustituirlo por el ejemplo.
7. El runtime sigue siendo .NET 10.
8. Arrancar el servicio. En el log debe aparecer la señal de salud. `SELECT TOP (1) * FROM dbo.IMPORTADOR_EJECUCION ORDER BY FECHA_INICIO_UTC DESC` debe mostrar `ACTIVA` y una `FECHA_SENAL_UTC` reciente.
9. Opcional: copiar `deploy/comprobar-salud.sh` al servidor y revisar a mano la unidad de ejemplo. No queda programada por este repositorio.

Esta corrección no añade columnas ni un script nuevo. Si `sql/05`, `sql/06` y `sql/07` ya se aplicaron, no hace falta repetirlos. La aplicación no migra el esquema al arrancar. Si las tablas no existen, la importación continúa y el seguimiento se acumula en `pendientes` hasta el límite.

Al actualizar el proceso, el directorio `pendientes` se crea solo. Los eventos antiguos que sigan en la raíz, con nombre `ejecucion-`, `intento-` o `evento-` más 32 hexadecimales, se envían y se borran. No mover a mano `salud.json`, `observaciones.json` ni `alertas-estado.json`. No vaciar `corruptos` hacia la bandeja.

## Reversión de la aplicación

Volver al binario anterior no borra `IMPORTADOR_*` ni el historial. El binario antiguo ignora esas tablas. Se puede dejar el esquema. No hace falta un `DROP` para revertir el programa. Si más adelante se quisieran quitar las tablas, sería una decisión manual y aparte: no está incluida aquí.

La bandeja `seguimiento` puede quedarse en disco. El proceso antiguo no la lee. No borrar `inbox/.organizacion`.

## Auditoría SQL ajena al importador

El seguimiento de arriba explica el importador. No explica qué otros usuarios ejecutaron en SQL Server ni sustituye un diagnóstico de rendimiento del servidor.

`sql/opcional/AuditoriaSqlServer.sql` es una plantilla comentada, separada de la migración, pensada para SQL Server en Linux (2017 o posterior; comprobar `SELECT @@VERSION`). Se apoya en la documentación de [SQL Server Audit](https://learn.microsoft.com/sql/relational-databases/security/auditing/sql-server-audit-database-engine) y de [Extended Events](https://learn.microsoft.com/sql/relational-databases/extended-events/extended-events).

Esa auditoría registra acciones de un principal. No guarda el valor anterior y posterior de cada columna. Para eso harían falta tablas temporales, Change Data Capture o triggers, y no forman parte de esta entrega. La plantilla no se activa sola, filtra escrituras, apunta a fichero con un tope de tamaño y advierte del permiso y del impacto.
