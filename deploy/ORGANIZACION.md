# Organización de archivos recibidos

Entrada de producción: `/home/tpv_recepcion`. Solo se examinan sus archivos directos.
`dos`, `face`, `verifactu`, `verifactuAnulaciones`, `recepcion` y archivos ajenos no se recorren ni modifican.
Un `.log` directo de la entrada, cuando está estable, se traslada a `logs` (por defecto `/home/tpv_recepcion/logs`).
Si el nombre ya existe, se conserva con `_REPETIDO_fechaUTC_identificador`. No se interpretan ni se publican en otro sitio.
`Paths:Logs` sigue siendo el registro del propio servicio y no es el destino de estos archivos.

## Facturas

El flujo XML/SQL conserva su transacción, identidad, hashes y recuperación actuales.
El XML se archiva en `procesados/yyyy/MM/dd`, sin nivel tienda, usando la fecha de expedición.
Si falta esa fecha, permanece el comportamiento anterior: fecha del nombre y, en último término, fecha del procesamiento.
Los errores XML siguen el flujo existente hacia `errores`. No se reorganizan los archivos históricos.

El organizador consulta, sin modificar SQL, las recepciones ARCHIVADO de importaciones procesadas,
duplicadas o en conflicto. Un PDF solo sale de entrada si se encuentra un único destino XML existente.
Se reconocen `nombre_sin_firmar.pdf` y `nombre_a4_sin_firmar.pdf`; el PDF normal también admite
el mismo nombre base del XML sin ese sufijo. Los PDF siguen la ruta real y el sufijo de colisión del XML.
No se espera a los PDF para importar SQL. Si llegan después, los recoge un ciclo posterior.
Una consulta SQL fallida deja el PDF pendiente. Varias recepciones del mismo origen producen una
advertencia y dejan el PDF pendiente: no se adivina a qué llegada pertenece. PDF sin XML importado,
o de un XML enviado a errores, permanecen en entrada.

## TXT e indicadores

1. Cada TXT con nombre `numero_nombre.txt` se espera hasta que esté estable y se traslada a `inbox`.
2. Un `bndnumero` nuevo en la entrada autoriza los TXT conservados pendientes de ese número exacto.
   Si aún quedan TXT de ese número en entrada, el indicador espera.
3. Se registra el conjunto concreto del lote ANTES de trasladar el indicador a `inbox`.
4. Se copian los TXT autorizados a `inboxOrganizado`, eliminando exclusivamente `numero_`.
   Se verifica el hash y se publica mediante un temporal privado y renombrado.
5. Los originales y los indicadores permanecen en `inbox`.

Cuando se repite un nombre en `inbox`, se conserva con `_REPETIDO_fechaUTC_identificador` antes
 de la extensión. Un indicador antiguo no autoriza archivos nuevos.
En `inboxOrganizado` cualquier nombre ocupado genera una incidencia: nunca se sobrescribe,
aunque los bytes sean iguales. El original permanece en `inbox`; se reintentará si el destino queda libre.
No se copia el indicador a `inboxOrganizado`.

El emisor debe publicar el bnd después de terminar los TXT del lote, y no solapar dos lotes
con el mismo número. Si el emisor sobrescribe un archivo antes de que el receptor pueda conservarlo,
el receptor no puede recuperar los bytes antiguos. El mismo límite existe si publica el indicador
antes de acabar las transferencias: observar tamaño estable no sustituye el protocolo del emisor.

## Reinicios y conservación

`inbox/.organizacion` contiene el registro local durable de movimientos, hashes, autorizaciones y copias.
NO borrar ni editar este directorio. Forma parte del estado del programa y debe incluirse en las copias de seguridad.
Los registros terminados pasan a `historial`, repartidos en subcarpetas; cada ciclo lee solo los pendientes.
Los archivos recibidos no se eliminan como limpieza periódica.
Un bloqueo exclusivo de `inbox/.organizador.lock` impide dos organizadores simultáneos sobre el mismo inbox.

Una intención de traslado se guarda antes del movimiento. Tras reiniciar:
- destino correcto existente: se finaliza el traslado; un origen existente se conserva como posible nueva llegada;
- solo origen correcto: se realiza el traslado;
- ninguno existe o los hashes no coinciden: se registra error y se conserva la intención pendiente.

La recuperación de copias distingue una publicación iniciada por este organizador de una colisión previa.
La corrupción de un registro detiene ese ciclo de organización y se registra; no se descarta silenciosamente.
XML sigue trabajando en su propio servicio de fondo.

Despliegue previsto: todas estas carpetas dentro del mismo filesystem local. No configurar destinos
en otros montajes, enlaces a otros volúmenes o redes: no se ha implementado un protocolo de copy+delete
entre volúmenes para esta organización. Los tests locales cubren caídas entre pasos; no simulan fallos
eléctricos del disco ni el servidor Ubuntu real.

## Configuración y despliegue

`Paths:Input=/home/tpv_recepcion`, `Paths:Processed=/home/tpv_recepcion/procesados`,
`Paths:Errors=/home/tpv_recepcion/errores` están preparados en appsettings.json.
Las variables de entorno `Paths__...` tienen prioridad: revisar las configuradas en el servidor.
`Organization` permite desactivar el organizador, cambiar Inbox/Organized/Logs, `MaxConcurrency`,
el intervalo y el timeout. `ScanIntervalSeconds` es la pausa entre pasadas, no un solapamiento:
el periodo es la duración de la pasada más ese intervalo. `MaxConcurrency` limita las esperas
del organizador y es independiente de `Processing:MaxConcurrency`.
Los valores vacíos de Inbox/Organized/Logs crean `inbox`, `inboxOrganizado` y `logs` bajo Input.
La estabilidad usa los parámetros existentes `Processing:StableChecks` y `StableCheckDelayMilliseconds`.

Para publicar desde el equipo de desarrollo:

```powershell
dotnet publish src/DbInda.Worker/DbInda.Worker.csproj -c Release -r linux-x64 --self-contained false -o artifacts/organizacion-linux-x64
```

Subir el contenido publicado con FileZilla a la carpeta de la aplicación, con el servicio detenido.
Conservar la configuración privada y las credenciales del servidor. Se requiere runtime .NET 10.
No subir carpetas de tests ni ejecutar scripts SQL: no hay cambios de esquema.

La plantilla `deploy/ticketstpv.service` ahora permite escritura en `/home/tpv_recepcion` y deja
`ProtectHome=read-only` con esa excepción. También se debe actualizar la unidad instalada y recargar
systemd antes de reiniciar; FileZilla no actualiza automáticamente la unidad en `/etc/systemd/system`.
El servicio sigue usando `ticketstpv`. El listado recibido muestra `/home/tpv_recepcion` con permisos 700
para `tpv_recepcion`: un administrador debe dar acceso de recorrido, lectura y escritura a `ticketstpv`
en entrada y carpetas de organización, y lectura a los archivos futuros (por ejemplo mediante ACL).
No se han cambiado usuarios, permisos ni servicios del servidor desde este proyecto.

Los mensajes van al proveedor ILogger existente. En la unidad suministrada stdout/stderr se guardan
 en `/opt/TicketsTPV/logs/worker.log`. Comprobar allí errores de permisos, colisiones, PDF ambiguos
 y la recepción/importación después de desplegar. La plantilla y los permisos deben verificarse en Ubuntu.