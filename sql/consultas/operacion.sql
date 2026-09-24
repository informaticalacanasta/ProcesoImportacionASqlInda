/*
    Consultas de solo lectura. Se pueden ejecutar enteras: no modifican datos.

    Antes del despliegue de las tablas de seguimiento solo funcionan las
    consultas marcadas COMO HISTORICO. El resto devuelve error si falta
    sql/05_CreateTracking.sql.

    Hoy = día civil de Romance Standard Time (Europe/Madrid).
    FECHA_PROCESADO se lee como hora local de la aplicación, sin convertir.
    FECHA_EXPEDICION es la fecha de la factura, no la de la importación.
*/

USE [TicketsTPV];
GO

/* 1. Estado general. La ejecución viva es la que no tiene parada.
      POSIBLE_INTERRUPCION significa ausencia de señal, no un registro
      escrito por el proceso que murió. */
SELECT *
FROM dbo.VW_IMPORTADOR_EJECUCION_SIN_SENAL;
GO

SELECT TOP (5) *
FROM dbo.IMPORTADOR_EJECUCION
ORDER BY FECHA_INICIO_UTC DESC;
GO

/* 2 y 4. Última importación correcta global.
      ULTIMA_IMPORTACION_UTC existe solo desde el despliegue.
      ULTIMA_FECHA_PROCESADO_LOCAL incluye el histórico y no es UTC.
      No usar MAX(FECHA_PROCESADO) sin filtrar el estado. */
SELECT
    ULTIMA_IMPORTACION_UTC,
    ULTIMA_FECHA_PROCESADO_LOCAL,
    CASE
        WHEN ULTIMA_IMPORTACION_UTC IS NULL THEN NULL
        ELSE DATEDIFF(MINUTE, ULTIMA_IMPORTACION_UTC, SYSUTCDATETIME())
    END AS MINUTOS_DESDE_IMPORTACION_UTC
FROM dbo.VW_IMPORTADOR_ULTIMA_IMPORTACION;
GO

/* HISTORICO. Misma pregunta con los datos que ya existían. */
SELECT
    MAX(FECHA_PROCESADO) AS ULTIMA_FECHA_PROCESADO_LOCAL,
    DATEDIFF(MINUTE, MAX(FECHA_PROCESADO), SYSDATETIME()) AS MINUTOS_APROX_SI_EL_SERVIDOR_ESTA_EN_ESA_HORA
FROM dbo.TICKET_RECEPCION
WHERE ESTADO IN ('PROCESADO', 'PROCESADO_CON_ADVERTENCIAS');
GO

/* 3. Última importación por tienda y TPV.
      Una tienda que nunca envió nada no aparece: hace falta
      Tracking:ExpectedSources para alertarla. */
SELECT *
FROM dbo.VW_IMPORTADOR_ULTIMA_POR_TIENDA_TPV
ORDER BY TIENDA, TPV;
GO

/* 5. Resumen de hoy. Cuenta intentos, no archivos ni eventos.
      IMPORTACIONES_NUEVAS no incluye duplicados ni conflictos. */
SELECT *
FROM dbo.VW_IMPORTADOR_RESUMEN_HOY;
GO

/* HISTORICO de hoy, interpretando FECHA_PROCESADO como hora local. */
SELECT
    SUM(CASE WHEN ESTADO = 'PROCESADO' THEN 1 ELSE 0 END) AS PROCESADOS,
    SUM(CASE WHEN ESTADO = 'PROCESADO_CON_ADVERTENCIAS' THEN 1 ELSE 0 END) AS CON_ADVERTENCIAS,
    SUM(CASE WHEN ESTADO = 'DUPLICADO' THEN 1 ELSE 0 END) AS DUPLICADOS,
    SUM(CASE WHEN ESTADO = 'CONFLICTO_MISMA_FACTURA' THEN 1 ELSE 0 END) AS CONFLICTOS,
    SUM(CASE WHEN ESTADO IN ('ERROR_XML', 'ERROR_PERMANENTE', 'ERROR_SQL') THEN 1 ELSE 0 END) AS ERRORES_ABIERTOS_O_CERRADOS_HOY
FROM dbo.TICKET_RECEPCION
WHERE FECHA_PROCESADO IS NOT NULL
  AND CAST(FECHA_PROCESADO AS date) = CAST(SYSDATETIME() AS date);
GO

/* 6. Error actual (la recepción puede haber borrado MENSAJE_ERROR en un reintento)
      y error histórico (el intento anterior permanece). */
SELECT *
FROM dbo.VW_IMPORTADOR_ERRORES_ABIERTOS
ORDER BY FECHA_ULTIMO_INTENTO DESC;
GO

SELECT TOP (200) *
FROM dbo.VW_IMPORTADOR_ERRORES_HISTORICOS
ORDER BY FECHA_INICIO_UTC DESC;
GO

/* 7. Intentos que fallaron y después tuvieron una importación nueva. */
SELECT TOP (200) *
FROM dbo.VW_IMPORTADOR_INTENTOS_RECUPERADOS
ORDER BY FIN_OK_UTC DESC;
GO

/* 8. Historial de una recepción. Sustituir el identificador. */
DECLARE @IdRecepcion bigint = 0;
SELECT *
FROM dbo.IMPORTADOR_INTENTO
WHERE ID_RECEPCION = @IdRecepcion
ORDER BY FECHA_INICIO_UTC, SECUENCIA;

SELECT *
FROM dbo.IMPORTADOR_EVENTO
WHERE ID_RECEPCION = @IdRecepcion
ORDER BY FECHA_UTC, SECUENCIA;

SELECT ID_RECEPCION, ESTADO, ESTADO_ARCHIVO, MENSAJE_ERROR, FECHA_PROCESADO, RUTA_FINAL
FROM dbo.TICKET_RECEPCION
WHERE ID_RECEPCION = @IdRecepcion;
GO

/* 9. Conflictos de factura. No son tickets nuevos. */
SELECT *
FROM dbo.VW_IMPORTADOR_CONFLICTOS
ORDER BY FECHA_PROCESADO DESC;
GO

/* 10. Archivado pendiente o fallido. No es un fallo de la inserción SQL. */
SELECT *
FROM dbo.VW_IMPORTADOR_ARCHIVADO_PENDIENTE
ORDER BY ID_RECEPCION;
GO

/* 11. Ejecuciones sin señal reciente (umbral de la vista: 3 minutos). */
SELECT * FROM dbo.VW_IMPORTADOR_EJECUCION_SIN_SENAL;
GO

/* 12. Rendimiento por hora UTC. Sube la media si el procesamiento se alarga.
      No mide cada sentencia SQL. */
SELECT TOP (48) *
FROM dbo.VW_IMPORTADOR_RENDIMIENTO_HORA
ORDER BY HORA_UTC DESC;
GO

/* 13. Los eventos pendientes de sincronizar no están en SQL.
      Están en Paths:Logs/seguimiento (o Tracking:OutboxDirectory),
      ficheros *.json. salud.json informa de OutboxPending y OldestOutboxUtc.
      Cuando SQL vuelve, el proceso los inserta y borra el fichero solo
      después de la confirmación. Reenviar no duplica: la clave es el GUID. */
SELECT 'Los pendientes locales no tienen fila SQL hasta el reenvío.' AS NOTA;
GO
