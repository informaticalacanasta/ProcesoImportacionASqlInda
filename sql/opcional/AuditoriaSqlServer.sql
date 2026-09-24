/*
    OPCIONAL. No forma parte de la migración del importador.
    No ejecutarlo junto con sql/05, sql/06 o sql/07.
    No está activo. Hay que revisar @@VERSION y completarlo a mano.

    Plataforma conocida del servicio: SQL Server en Linux (unidad mssql-server).
    Eso implica SQL Server 2017 o posterior. Comprobar:

        SELECT @@VERSION;

    Documentación usada como referencia:
    - SQL Server Audit
      https://learn.microsoft.com/sql/relational-databases/security/auditing/sql-server-audit-database-engine
    - Extended Events
      https://learn.microsoft.com/sql/relational-databases/extended-events/extended-events

    Qué cubre y qué no:
    - El seguimiento IMPORTADOR_* explica qué hizo el importador.
    - SQL Server Audit explica qué principal ejecutó qué acción.
    - Extended Events sirve para diagnosticar esperas o consultas concretas.
    - Ninguno de los dos guarda, por sí solo, el valor anterior y el posterior
      de cada columna. Para eso harían falta tablas temporales de sistema,
      Change Data Capture o triggers. Es otra necesidad y no está incluida.

    Permisos orientativos: ALTER ANY SERVER AUDIT y CONTROL SERVER para la
    auditoría de servidor; ALTER ANY DATABASE AUDIT en TicketsTPV.
    El login de la aplicación no debe tenerlos.

    Destino: fichero, no el registro de aplicación de Windows (en Linux no aplica).
    Retención del ejemplo: 5 ficheros de 64 MB. Ajustar al disco.
    Impacto: auditar todos los SELECT de la base es caro. Este borrador solo
    propone escrituras de otros usuarios sobre las tablas de ticket, y deja
    la sesión de Extended Events desactivada.

    Filtro: excluye al login ticketstpv_app para no duplicar el seguimiento
    del propio importador. Cambiar el nombre si el login es otro.
*/

/*
USE [master];
GO

CREATE SERVER AUDIT [TicketsTPV_Usuarios_Opcional]
TO FILE
(
    FILEPATH = N'/var/opt/mssql/audit/',
    MAXSIZE = 64 MB,
    MAX_ROLLOVER_FILES = 5,
    RESERVE_DISK_SPACE = OFF
)
WITH (QUEUE_DELAY = 1000, ON_FAILURE = CONTINUE);
GO

CREATE SERVER AUDIT SPECIFICATION [TicketsTPV_LoginFallido_Opcional]
FOR SERVER AUDIT [TicketsTPV_Usuarios_Opcional]
ADD (FAILED_LOGIN_GROUP);
GO

USE [TicketsTPV];
GO

CREATE DATABASE AUDIT SPECIFICATION [TicketsTPV_EscriturasAjenas_Opcional]
FOR SERVER AUDIT [TicketsTPV_Usuarios_Opcional]
ADD (INSERT, UPDATE, DELETE ON OBJECT::dbo.TICKET BY [public]),
ADD (INSERT, UPDATE, DELETE ON OBJECT::dbo.TICKET_RECEPCION BY [public])
WITH (STATE = OFF);
GO

-- Activar solo después de crear el directorio y revisar el filtro.
-- ALTER SERVER AUDIT [TicketsTPV_Usuarios_Opcional] WITH (STATE = ON);
-- ALTER DATABASE AUDIT SPECIFICATION [TicketsTPV_EscriturasAjenas_Opcional] WITH (STATE = ON);

-- Extended Events de diagnóstico, parado. No captura el texto de todas las consultas.
-- CREATE EVENT SESSION [TicketsTPV_ErroresSql_Opcional] ON SERVER
-- ADD EVENT sqlserver.error_reported(WHERE severity >= 16)
-- ADD TARGET package0.event_file(SET filename = N'/var/opt/mssql/audit/errores.xel', max_file_size = 32, max_rollover_files = 3)
-- WITH (STARTUP_STATE = OFF);
*/
GO
