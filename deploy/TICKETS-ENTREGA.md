# Entrega de tickets XML y PDF

El servicio .NET archiva los tickets en `Tickets/Procesados`. Después publica en
`/home/tpv_recepcion/Tickets/Salida/ID_RECEPCION/` un conjunto con el XML, el PDF
normal, el PDF A4 y `manifest.json`. La carpeta aparece cuando las tres copias han
sido verificadas. Si un PDF llega tarde, el servicio espera y reintenta; no marca el
conjunto como entregado antes de tiempo. Los originales se conservan en
`Tickets/Procesados`.

El publicador consulta las recepciones `ARCHIVADO` con estado `PROCESADO` o
`PROCESADO_CON_ADVERTENCIAS`. No exporta duplicados, conflictos ni errores. El XML
se verifica contra el SHA-256 guardado en SQL y cada PDF contra su archivo de
origen. Un directorio ya publicado con `manifest.json` conserva su identidad;
si procede de la versión antigua que solo copiaba XML, el publicador completa los
dos PDF cuando estén disponibles.

## Ubuntu

El `appsettings.json` de producción ya habilita `TicketDelivery`. Al desplegar una
versión nueva, comprobar que `/etc/ticketstpv/ticketstpv.env` no lo anula con
`TicketDelivery__Enabled=false` ni apunta a la ruta antigua. Valores esperados:

```ini
TicketDelivery__Enabled=true
TicketDelivery__Directory=/home/tpv_recepcion/Tickets/Salida
TicketDelivery__MinimumReceptionId=0
TicketDelivery__ScanIntervalSeconds=30
```

`MinimumReceptionId=0` incluye los tickets elegibles que ya se procesaron y aún
conservan sus tres archivos archivados. No es un cursor: un ticket cuyo PDF llegue
después se vuelve a consultar. Cada recepción se publica en su propia carpeta
numérica. Conservar `manifest.json` y la carpeta de la recepción: forman el registro
de publicación. El servicio necesita permisos de lectura sobre `Tickets/Procesados`
y escritura sobre `Tickets/Salida`.

Publicar los binarios actualizados y reiniciar el servicio cuando se quiera poner
en marcha; editar solo el script de Windows no activa el publicador. Si
`Tickets/Salida` sigue vacía, el descargador continuará con los pedidos y mostrará
que no hay tickets publicados.

## Windows

`Descargar-Inbox-Unificado.ps1` toma cada conjunto completo de `Tickets/Salida` y
copia XML y ambos PDF a `InboxPrueba`, verificando SHA-256 en los archivos nuevos.
No envía tickets a `inbox` y conserva la publicación de Ubuntu. Si un XML ya estaba
en `InboxPrueba`, una ejecución posterior copia los PDF que falten. Los archivos
que ya existan se omiten por nombre, sin volver a comparar su contenido.

Los pedidos continúan el recorrido `Pedidos/Salida` → `InboxPrueba` → `inbox`.
Después de verificar ambas copias, el script elimina el TXT de `Pedidos/Salida`.
Cada paso se muestra con fecha, hora y rutas y se guarda en el log del script.

Para actualizar la copia publicada desde Windows:

```powershell
dotnet publish src/DbInda.Worker/DbInda.Worker.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
```

Copiar `deploy/Descargar-Inbox-Unificado.ps1` a
`C:\Users\Usuario\Desktop\Script\Descargar-Inbox-Unificado.ps1`. No ejecutar también
las tareas antiguas de descarga para evitar duplicados.
