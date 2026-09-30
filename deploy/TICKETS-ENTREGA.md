# Entrega independiente de tickets XML

El servicio .NET se ejecuta en Ubuntu. `Descargar-Inbox-Unificado.ps1` se ejecuta en Windows.
El programa y el script de pedidos existente no se modifican.

## Ubuntu

Configurar en `/etc/ticketstpv/ticketstpv.env`:

```ini
TicketDelivery__Enabled=true
TicketDelivery__Directory=/home/tpv_recepcion/tickets/paraDescargar
TicketDelivery__MinimumReceptionId=0
TicketDelivery__ScanIntervalSeconds=30
```

La función está desactivada por defecto para evitar una descarga histórica accidental.
Antes de activarla, elegir el ID mínimo de recepción (inclusive). **0 incluye todos
los tickets históricos elegibles que aún conserven su XML archivado.** Un ID mayor
permite comenzar por una recepción concreta. No es un cursor: los fallos se vuelven
a consultar, incluso si se completan más tarde que otras recepciones.

Solo publica recepciones `ARCHIVADO` y `PROCESADO` o `PROCESADO_CON_ADVERTENCIAS`.
Excluye duplicados, conflictos y errores. No modifica SQL ni requiere nuevas tablas.
Publica el XML sin cambiar sus bytes o su nombre. No exporta PDF ni convierte a TXT.
El consumidor de Windows debe admitir estos XML; esta entrega no añade un importador a TPVISION.

Cada recepción tiene `paraDescargar/ID/ticket.xml` y `manifest.json`. La copia se
prepara en `.staging`, se verifica contra el SHA-256 de SQL y se publica renombrando
la carpeta. Staging y salida deben estar en el mismo sistema de archivos local.
El XML original de `procesados` se conserva. Los errores de publicación o SQL se
reintentan sin detener el procesamiento de pedidos.

**Conservar las carpetas ID y sus manifest.json aunque ya no tengan XML.** Son el
registro persistente que impide volver a publicar tickets descargados. Incluirlos
en las copias de seguridad. No configurar esta ruta como carpeta de entrada, pedidos,
inbox, procesados o errores. La unidad systemd existente permite escribir bajo
`/home/tpv_recepcion`; verificar permisos para el usuario del servicio y para SSH.

## Windows

Copiar `deploy/Descargar-Inbox-Unificado.ps1` a `C:\Users\Usuario\Desktop\Script`.
Es un solo archivo ejecutable que sustituye la ejecución de los dos scripts anteriores.
Por defecto pide una vez la contraseña SSH; también admite la variable de entorno
`TICKETS_SSH_PASSWORD` o `-UsarClaveSsh` para autenticación por clave. No guarda
la contraseña en el repositorio ni modifica el script de pedidos anterior.
Para la tarea programada puede leer `inbox-unificado-ssh.secret`, generado con
`Read-Host -AsSecureString | ConvertFrom-SecureString`; Windows lo cifra para el
usuario que lo creó. La tarea debe ejecutarse con ese mismo usuario.

```powershell
.\Descargar-Inbox-Unificado.ps1
```

Los pedidos siguen el recorrido probado: `inboxOrganizado` de Ubuntu →
`InboxPrueba` → `inbox` → borrado de la salida de Ubuntu. Se comprueba su tamaño.

Los tickets XML siguen otro recorrido dentro del mismo script: `paraDescargar/ID` de
Ubuntu → `InboxPrueba`. Se verifica SHA-256. **Los tickets terminan ahí**: este script
no tiene una opción de enviarlos a inbox ni borra sus XML de Ubuntu. Una ejecución
posterior vuelve a comprobar el archivo ya descargado. Si el nombre coincide con un
archivo de distinto contenido, informa de colisión y conserva ambos orígenes.

No iniciar también los scripts antiguos como tarea programada: se duplicarían
las descargas de pedidos. Para programación sin interacción, configurar la
autenticación SSH antes; la ejecución manual pide la contraseña una vez.

## Publicación desde Windows para Ubuntu

```powershell
dotnet publish src/DbInda.Worker/DbInda.Worker.csproj -c Release -r linux-x64 --self-contained false -o artifacts/tickets-linux-x64
```

Desplegar los binarios en Ubuntu conservando la configuración privada actual.
Requiere el runtime .NET 10. Activar las variables anteriores y reiniciar el servicio
durante el despliegue. La compilación local no despliega ni ejecuta descargas.
