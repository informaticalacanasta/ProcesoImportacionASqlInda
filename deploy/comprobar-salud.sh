#!/bin/sh
# Comprobación EXTERNA de solo lectura. No la instala el proyecto.
# El proceso muerto no puede avisar de su propia muerte: esta comprobación
# la ejecuta systemd, cron u otro supervisor.
#
# Requisitos: sqlcmd y un fichero de entorno que NO está en Git, con
# SQLCMDPASSWORD o una cadena ya autenticada. No poner la contraseña aquí.
#
# Umbral por defecto: 180 segundos (Tracking:StaleSignalMinutes = 3).
# Si se cambia la configuración, hay que cambiar también UMBRAL.
#
# Uso manual:
#   SQLCMDSERVER=127.0.0.1,1433 SQLCMDUSER=ticketstpv_app ./comprobar-salud.sh
# Códigos: 0 sana, 1 SQL no consultable, 2 sin señal reciente.

set -eu
UMBRAL="${TICKETSTPV_SENAL_SEGUNDOS:-180}"
SQL="${TICKETSTPV_SQL:-/opt/mssql-tools18/bin/sqlcmd}"

RESULTADO="$("$SQL" -C -d TicketsTPV -h -1 -W -Q "SET NOCOUNT ON; SELECT ISNULL(MIN(DATEDIFF(SECOND, FECHA_SENAL_UTC, SYSUTCDATETIME())), -1) FROM dbo.IMPORTADOR_EJECUCION WHERE FECHA_PARADA_UTC IS NULL;")" || exit 1
RESULTADO="$(printf '%s' "$RESULTADO" | tr -d '[:space:]')"

if [ -z "$RESULTADO" ] || [ "$RESULTADO" = "-1" ]; then
  echo "No hay ejecución activa." >&2
  exit 2
fi

if [ "$RESULTADO" -gt "$UMBRAL" ]; then
  echo "Señal antigua: ${RESULTADO}s (umbral ${UMBRAL}s)." >&2
  exit 2
fi

echo "Señal reciente: ${RESULTADO}s."
exit 0
