# Revisión de lógica y recuperación del importador .NET

Fecha: 30 de septiembre de 2026. Referencia revisada: `37976f7`.

**Estado: solo documentación. No se ha corregido el código, cambiado la configuración, desplegado una versión ni intervenido en Ubuntu o SQL durante esta revisión.**

## Conclusión

El funcionamiento normal está respaldado por las pruebas existentes y por las importaciones reales observadas anteriormente. La nueva organización de carpetas no exige que Tickets y Pedidos tengan los mismos mecanismos internos.

Sin embargo, no sería correcto afirmar que no quedan fallos. Se identifican siete problemas o limitaciones de lógica que afectan principalmente a archivos bloqueados, recuperación después de fallos y concurrencia. No hay evidencia en esta revisión de que hayan causado pérdida de los pedidos o tickets ya procesados en producción.

Los hallazgos se basan en la lectura de las ramas y sus llamadas. Los escenarios indicados permiten preparar pruebas de regresión, pero **no se han añadido ni ejecutado nuevas reproducciones específicas**. Tampoco se ha inspeccionado el esquema SQL real en esta revisión: los comentarios sobre restricciones de SQL corresponden a los scripts del repositorio.

Prioridades: **P1** para riesgo de integridad o bloqueo general de un flujo; **P2** para recuperación incompleta o bloqueo de un archivo en un caso condicionado.

## R01 — P1: la espera de estabilidad puede detener nuevas pasadas indefinidamente

Ubicaciones:

- `src/DbInda.Worker/Inbound/FileReadinessChecker.cs:51`.
- `src/DbInda.Worker/Workers/TicketStagingWorker.cs:44`.
- `src/DbInda.Worker/Orders/PedidoProcessor.cs:45`.
- `src/DbInda.Worker/Orders/PedidoFileMirror.cs:117`.

**Qué ocurre.** La espera continúa mientras el archivo exista y el token no se cancele. Si el archivo no se puede abrir o nunca se estabiliza, no existe un tiempo máximo propio. `FileSystemStabilityProbe` convierte el permiso denegado en una observación fallida; el lector vuelve a intentarlo indefinidamente y solo escribe el motivo genérico a nivel Debug.

`TicketStagingWorker` espera a que termine todo el `Parallel.ForEachAsync` antes de enumerar otra vez la entrada. Basta un archivo bloqueado para impedir que empiece la siguiente pasada y descubra archivos que llegaron después. Si se ocupan todos los puestos, también pueden quedarse sin atender otros archivos de la misma enumeración. El importador de pedidos utiliza una estructura similar. En el espejo de pedidos existe una observación previa: el riesgo allí aparece si el archivo deja de ser legible o empieza a cambiar después de esa primera observación.

**Escenario de comprobación.** Mantener un XML existente pero no legible para el usuario del servicio. Iniciar una pasada y añadir después un XML válido. Comprobar que la primera pasada no termina y que el nuevo XML no llega a Pendientes hasta resolver el bloqueo o reiniciar. Repetir con un pedido bloqueado en Pendientes.

**Corrección propuesta.** Limitar la espera por archivo, conservar el original, liberar el puesto y reintentarlo en otra pasada. Distinguir en los mensajes permiso denegado, archivo ocupado y contenido todavía cambiante. No sustituir el usuario del servicio por root.

La incidencia de permisos vista anteriormente es compatible con este camino del código; no se ha reproducido aquí la saturación de todos los puestos.

## R02 — P1: el archivado no comprueba siempre que el origen siga siendo el contenido importado

Ubicaciones:

- `src/DbInda.Worker/Files/XmlFileArchiver.cs:62`.
- `src/DbInda.Worker/Processing/XmlArchiveReconciler.cs:131`.
- `src/DbInda.Worker/Orders/PedidoFileArchive.cs:20`.

**Qué ocurre.** `XmlFileArchiver.MoveToExactAsync` recibe el hash esperado, pero en el traslado dentro del mismo volumen mueve el origen sin verificarlo. El reconciliador puede llamar a este método con el hash de una recepción anterior y después finalizar el archivado en SQL.

En pedidos, `PedidoFileArchive.Place` comprueba el hash del destino cuando ya existe, pero no el del origen. Si el destino coincide con el hash esperado, borra el origen. Si no existe destino, mueve el origen directamente.

**Condición necesaria.** El archivo de origen debe haberse modificado o sustituido después de leerlo para importar, o entre un intento anterior y la recuperación. Esto no implica que un duplicado normal cause el problema. Es relevante ante reanudación de una escritura después de una pausa, reposición manual o recuperación con un nombre reutilizado.

**Escenarios de comprobación.**

1. Calcular el hash de un XML A, sustituir el origen por B y llamar al archivador con el hash de A. La rama actual del mismo volumen mueve B sin rechazarlo.
2. Crear un pedido archivado A, poner B en el origen y llamar a `Place` con el hash de A. Al reconocer A en el destino, el método elimina B del origen.

**Impacto.** La ruta de SQL puede terminar asociada a bytes diferentes de los importados. En la rama de pedido ya existente puede eliminarse una nueva versión sin importarla.

**Corrección propuesta.** Verificar la identidad del origen antes de moverlo o deduplicarlo y conservar cualquier contenido diferente para una nueva recepción. El diseño debe contemplar también la carrera entre comprobación y movimiento; añadir únicamente una comparación de hash no garantiza seguridad frente a un escritor externo que siga activo. Probar expresamente la recuperación de una ruta reutilizada.

## R03 — P1: dos versiones simultáneas de una factura pueden insertarse como tickets distintos

Ubicaciones:

- `src/DbInda.Worker/Processing/TicketImportProcessor.cs:175` y `:182`.
- `src/DbInda.Worker/Persistence/TicketRepository.cs:29`.
- `sql/02_Indexes_Recommended.sql:71`.

**Qué ocurre.** La búsqueda por identidad de factura se hace antes de abrir la transacción de inserción. Dos procesamientos pueden comprobar simultáneamente que la factura no existe e insertar ambos. La restricción única por hash evita dos tickets con bytes idénticos, pero no dos contenidos diferentes para la misma identidad.

**Escenario de comprobación.** Procesar en paralelo dos XML con rutas distintas, igual identidad de factura y hashes diferentes; sincronizar ambos justo después de la consulta de identidad para que los dos observen que no existe.

**Impacto.** Si la base tiene únicamente las restricciones publicadas en el repositorio, las dos versiones pueden quedar importadas, en vez de clasificar una como `CONFLICTO_MISMA_FACTURA`.

**Corrección propuesta.** Definir y proteger la identidad de negocio de forma atómica entre consulta e inserción, incluyendo la concurrencia entre procesos. Valorar una restricción única y el tratamiento de su colisión, o una serialización transaccional por identidad. La solución debe respetar la semántica actual de NIF, serie, número, fecha, tienda y TPV y los valores nulos.

**Limitación ya conocida.** El propio script SQL documenta expresamente esta carrera y deja pendiente la restricción de identidad. No es una regresión demostrada del cambio de carpetas. No se ha comprobado si el servidor tiene restricciones adicionales.

## R04 — P2: un registro de recuperación de pedidos con error puede bloquear el espejo completo

Ubicación: `src/DbInda.Worker/Orders/PedidoFileMirror.cs:60` y `:105`.

**Qué ocurre.** Cada pasada ejecuta `RecoverAsync` antes de enumerar los pedidos nuevos. Los registros `Prepared` se recuperan mediante un bucle sin aislamiento por registro. Si `ResumeAsync` lanza una excepción de acceso, lectura o publicación, se abandona la pasada completa. El worker reintenta, pero vuelve a encontrar el mismo registro antes de alcanzar los nuevos pedidos.

**Escenario de comprobación.** Dejar un registro `Prepared` con su copia íntegra y un destino que sea un directorio en lugar de un archivo, o impedir la lectura de un archivo usado por ese registro. Añadir otro pedido correcto a Recepcion. Verificar que la excepción del primero impide copiar el segundo a Pendientes en cada pasada.

**Impacto.** Los pedidos nuevos pueden acumularse en Recepcion mientras otro registro no relacionado sigue averiado. El organizador y la importación de archivos que ya estaban en Pendientes pueden seguir funcionando.

**Corrección propuesta.** Aislar el fallo de cada registro y de cada copia huérfana, registrar su estado para reintento y continuar con los demás. Mantener la cancelación del servicio como cancelación, sin ocultarla como un error recuperable.

## R05 — P2: la recuperación de pedidos no resuelve un destino ocupado ni sincroniza todas las rutas nuevas

Ubicaciones:

- `src/DbInda.Worker/Orders/PedidoProcessor.cs:186` y `:196`.
- `src/DbInda.Worker/Orders/PedidoFileArchive.cs:20`.
- `src/DbInda.Worker/Orders/PedidoRepository.cs:313`.

**Qué ocurre.** Cuando el hash ya figura como procesado, `ArchiveProcessedAsync` reutiliza `RutaFinal` si su carpeta padre existe. No vuelve a planificar si esa ruta está ocupada por contenido distinto. `Place` rechaza el destino y el siguiente ciclo vuelve a elegirlo.

Además, si la carpeta padre anterior no existe, se calcula otro destino; esta rama de recuperación no actualiza la nueva ruta en SQL. La actualización disponible en la rama de importación inicial es posterior al traslado y el repositorio absorbe sus errores SQL, por lo que tampoco constituye una recuperación durable de la ruta.

**Escenarios de comprobación.**

1. Simular una recepción ya procesada cuya `RutaFinal` existe con otro contenido. Dejar el pedido correcto en Pendientes y ejecutar dos ciclos: continúa atascado en el mismo destino.
2. Simular una recepción procesada cuya carpeta de archivo se haya perdido y cuya ruta sea distinta de la que se calcularía hoy. El pedido puede moverse al nuevo destino sin actualizar `DS_RUTA_FINAL`.

**Corrección propuesta.** Resolver la colisión conservando ambos archivos y persistir el destino real con un protocolo recuperable. Si SQL no acepta la nueva ruta, conservar suficiente estado para reintentarlo; no dar el trabajo por terminado dejando una referencia incorrecta. No requiere reinsertar el pedido.

## R06 — P2: un plan de organización cuyo origen cambió puede quedar bloqueado permanentemente

Ubicaciones: `src/DbInda.Worker/Files/ReceivedFileOrganizer.cs:65`, `:377` y `:413`.

**Qué ocurre.** El organizador guarda un plan con el hash del origen antes de moverlo. Si el origen cambia antes del movimiento, se conserva el archivo y el plan continúa como `Planned`. En la recuperación se rechaza el hash distinto, pero no se resuelve ni se aparta ese plan. `HasPending` impide que el archivo actual genere uno nuevo.

**Escenario de comprobación.** Guardar un plan A sin destino, dejar en su ruta origen un archivo estable B con otro hash y ejecutar dos pasadas. Ambas intentan recuperar A y rechazan B; ninguna crea un plan para B.

**Impacto.** El archivo puede quedar atascado aunque ya sea legible y estable. Afecta al flujo de organización de TXT, PDF o logs; no implica que falle el resto de la pasada.

**Corrección propuesta.** Dar al plan desactualizado un estado explícito que conserve la evidencia, y permitir tratar el contenido actual como una llegada distinta. En TXT y marcadores es necesario mantener la asociación de lote; no basta con borrar el JSON antiguo.

## R07 — P2: un JSON ilegible del organizador impide organizar todos los archivos nuevos

Ubicaciones: `src/DbInda.Worker/Files/OrganizationJournal.cs:24` y `src/DbInda.Worker/Files/ReceivedFileOrganizer.cs:64`.

**Qué ocurre.** `OrganizationJournal.Load` deserializa todos los JSON activos sin aislar errores de lectura o de formato. Una excepción de un solo registro se propaga y termina la pasada antes de enumerar los archivos recién llegados. El worker permanece vivo y vuelve a intentarlo, pero el archivo problemático continúa en el mismo lugar.

**Escenario de comprobación.** En un entorno de pruebas, colocar un JSON truncado en `Pedidos/Recepcion/.organizacion`, añadir un TXT nuevo con su marcador y ejecutar dos pasadas. Ninguna podrá organizar la nueva llegada. Lo mismo impide organizar los PDF y logs que utiliza este organizador. No bloquea por sí solo la importación de XML ni los pedidos que ya están en Pendientes.

**Corrección propuesta.** Validar y aislar registros ilegibles conservándolos para diagnóstico. Un registro dañado puede contener la relación entre un marcador y sus TXT: no se debe ignorar y autorizar la publicación o limpieza de un lote cuyo estado sea incierto. Se necesita un estado de recuperación que deje avanzar las llegadas independientes y suspenda de forma explícita el lote afectado.

La escritura habitual utiliza un temporal y un cambio de nombre; esta revisión no afirma que un cierre normal produzca JSON truncados. El hallazgo se refiere a la falta de aislamiento si aparece corrupción o un error de acceso sobre un registro activo.

## Protecciones que sí están presentes

- La importación de un ticket guarda su cabecera, detalles y actualización de recepción dentro de una transacción. Los scripts incluyen unicidad por hash, distinta de la identidad de negocio pendiente de R03.
- Pedidos conserva el original mientras prepara y verifica una copia. El diario permite recuperar copias preparadas después de un corte; R04 se refiere al aislamiento de una recuperación averiada, no a la ausencia de recuperación.
- La limpieza de Recepcion usa evidencia persistida, espera desde que un archivo resulta elegible y se coordina con el bloqueo del organizador. Un pedido sin copia confirmada en el diario no es elegible para esa limpieza.
- La limpieza de Recepcion se limita a sus archivos directos y excluye enlaces detectados como `ReparsePoint`. No recorre las carpetas de histórico Procesados para eliminarlas por antigüedad.
- El descubrimiento de XML tiene un escáner periódico además del aviso del sistema de archivos, por lo que perder un evento del watcher no es suficiente para perder una importación.

## Limitaciones adicionales que conviene conocer

### El estado de salud está centrado en XML

`ImportHealthWorker.ListPending` (`src/DbInda.Worker/Workers/ImportHealthWorker.cs:228`) cuenta XML en la raíz y en Tickets/Pendientes. No incluye la cola de pedidos ni los PDF sin asociar. Por eso `ESPERANDO_DATOS` no significa necesariamente que todos los tipos de archivo estén resueltos.

También puede indicar `SANO` mientras hay un XML ilegible, hasta que se alcance el umbral de falta de progreso configurado. No es correcto afirmar que nunca detectará el atasco: existe `PendingStalled` y, con la configuración actual, el umbral es de diez minutos. Una mejora sería mostrar salud por flujo y avisar antes cuando la causa sea un permiso denegado.

### Un PDF puede quedar pendiente tras reenviar el mismo XML

`ArchivedInvoiceLookup` devuelve todas las rutas archivadas de una misma ruta de origen; `ReceivedFileOrganizer` exige exactamente una. Si se archivan varias recepciones del mismo nombre, la consulta puede resultar ambigua permanentemente para un PDF tardío. Es una protección deliberada para no asignarlo a la factura equivocada, no una pérdida del PDF. Resolverlo exige definir una regla fiable de asociación; no conviene escoger simplemente la última recepción.

### La documentación de despliegue conserva rutas antiguas

`deploy/ORGANIZACION.md` y `deploy/TICKETS-ENTREGA.md` todavía contienen ejemplos como `inboxOrganizado`, `pedidos` o `tickets/paraDescargar`. La configuración de `src/DbInda.Worker/appsettings.json` utiliza las rutas nuevas. Los documentos deben actualizarse cuando se autorice esa tarea para evitar recuperar rutas antiguas al copiar comandos. El script de Windows no se ha modificado.

## Verificación realizada

- Lectura de los recorridos de recepción, estabilidad, importación, archivado, recuperación, PDF, duplicados, limpieza y seguimiento, además de los scripts SQL y sus pruebas relacionadas.
- Ejecución de `dotnet test DbInda.sln -c Release --no-restore --verbosity quiet`: **289 pruebas correctas, 27 omitidas, 0 fallidas; 316 en total**.
- Para esta ejecución se fijó `DBINDA_TEST` solo en el proceso de pruebas a una conexión local no disponible (`127.0.0.1,1`), evitando utilizar SQL de producción. Las comprobaciones de integración SQL quedan fuera de esta validación.
- No se han añadido nuevas pruebas, modificado fuentes o configuración, publicado binarios ni ejecutado cambios en Ubuntu.

Que pasen las pruebas existentes confirma los escenarios que cubren, pero no invalida los caminos excepcionales descritos arriba. No se afirma haber probado todas las combinaciones de fallos ni una carga equivalente a treinta tiendas.

## Relación con SOLID y orden propuesto

La separación entre procesamiento, persistencia y archivado, junto con las interfaces existentes, es una base razonable. `ReceivedFileOrganizer` concentra varias responsabilidades y algunas clases usan directamente el sistema de archivos; son oportunidades de diseño, pero el tamaño de una clase o la falta de una interfaz no prueban por sí solos un fallo.

Antes de una refactorización general de SOLID, propondría abordar R01 y R02 con pruebas de regresión, decidir la protección SQL de R03 en un entorno de pruebas y después resolver R04–R07. Las carpetas `.mirror` y `.staging` mantienen su función: ninguno de estos hallazgos justifica eliminarlas ni duplicarlas en Tickets por simetría.

**Todas las correcciones descritas quedan pendientes. Este documento no las aplica.**
