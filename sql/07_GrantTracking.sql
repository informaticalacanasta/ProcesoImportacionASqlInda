/*
    Permisos mínimos del seguimiento para ticketstpv_app.
    Ejecución MANUAL, después de sql/05_CreateTracking.sql y sql/06_Vistas_Seguimiento.sql.
    No concede DELETE. La retención SQL queda desactivada hasta que se
    configure y se descomente el bloque final.
    No toca tickets.
*/

USE [TicketsTPV];
GO

GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.IMPORTADOR_EJECUCION TO [ticketstpv_app];
GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.IMPORTADOR_INTENTO TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.IMPORTADOR_EVENTO TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ESTADO TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_EJECUCION_SIN_SENAL TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ULTIMA_IMPORTACION TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ULTIMA_POR_TIENDA_TPV TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_RESUMEN_HOY TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ERRORES_ABIERTOS TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ERRORES_HISTORICOS TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_INTENTOS_RECUPERADOS TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_CONFLICTOS TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_ARCHIVADO_PENDIENTE TO [ticketstpv_app];
GRANT SELECT ON OBJECT::dbo.VW_IMPORTADOR_RENDIMIENTO_HORA TO [ticketstpv_app];
GO

/*
    Solo si Tracking:Retention:Enabled se pone a true y HistoryDays tiene
    un plazo elegido por la operación. No es un plazo legal.

    GRANT DELETE ON OBJECT::dbo.IMPORTADOR_EVENTO TO [ticketstpv_app];
    GRANT DELETE ON OBJECT::dbo.IMPORTADOR_INTENTO TO [ticketstpv_app];
    GRANT DELETE ON OBJECT::dbo.IMPORTADOR_EJECUCION TO [ticketstpv_app];
*/
GO
