/*
    DbInda — índices recomendados (revisión MANUAL, no ejecutar desde la app).

    Arranque: solo los índices de consultas básicas de importación/ventas.
    El resto queda comentado hasta tener consultas reales y planes de ejecución.

    Ya creados en 01_CreateTables.sql (integridad, no repetir):
    - PK clustered de cada tabla
    - UX_TICKET_HASH_SHA256
    - UX_TICKET_IDENTIDAD_FACTURA: PENDIENTE (varios días del mismo TPV)
    - UNIQUE (ID_TICKET, NUM_LINEA / NUM_ORDEN) en tablas hijas:
      cubren el join por ID_TICKET (prefijo izquierdo). No se duplican aquí.

    Este script (arranque) incluye IX_TICKET_IDENTIDAD_FACTURA_LOOKUP:
    índice NO UNIQUE para el SELECT de CONFLICTO_MISMA_FACTURA.
    No sustituye a UX_TICKET_IDENTIDAD_FACTURA.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* -------------------------------------------------------------------------- */
/* Arranque                                                                   */
/* -------------------------------------------------------------------------- */

-- Ventas por día o rango de fechas.
CREATE NONCLUSTERED INDEX IX_TICKET_FECHA_EXPEDICION
    ON dbo.TICKET (FECHA_EXPEDICION);
GO

-- Ventas de una tienda en un rango de fechas.
CREATE NONCLUSTERED INDEX IX_TICKET_TIENDA_FECHA
    ON dbo.TICKET (TIENDA, FECHA_EXPEDICION);
GO

-- Ventas por producto / CodigoCentral, luego join a TICKET por fecha.
CREATE NONCLUSTERED INDEX IX_TICKET_DETALLE_CODIGO_CENTRAL
    ON dbo.TICKET_DETALLE (CODIGO_CENTRAL)
    WHERE CODIGO_CENTRAL IS NOT NULL;
GO

-- ¿Este XML (mismos bytes) ya llegó? Listar retransmisiones.
CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_HASH
    ON dbo.TICKET_RECEPCION (HASH_SHA256)
    WHERE HASH_SHA256 IS NOT NULL;
GO

-- Reintentos / pendientes tras caída de SQL o reinicio.
CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_ESTADO
    ON dbo.TICKET_RECEPCION (ESTADO, FECHA_ULTIMO_INTENTO)
    WHERE ESTADO IN ('PENDIENTE', 'PROCESANDO', 'ERROR_TEMPORAL');
GO

-- Reconciliación de archivo físico (PENDIENTE / ARCHIVANDO / ERROR_ARCHIVO).
CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_ARCHIVO_PENDIENTE
    ON dbo.TICKET_RECEPCION (ESTADO_ARCHIVO)
    WHERE ESTADO_ARCHIVO IN ('PENDIENTE', 'ARCHIVANDO', 'ERROR_ARCHIVO');
GO

-- Lookup provisional de CONFLICTO_MISMA_FACTURA (consulta real del importer).
-- NO UNIQUE. Sustituir cuando se decida UX_TICKET_IDENTIDAD_FACTURA
-- (NIF+SERIE+NUMERO o NIF+SERIE+NUMERO+FECHA, según XML de varios días).
-- LIMITACIÓN: este índice no impide dos INSERT concurrentes con la misma
-- identidad y hashes distintos. Eso solo lo cubrirá la UNIQUE definitiva.
CREATE NONCLUSTERED INDEX IX_TICKET_IDENTIDAD_FACTURA_LOOKUP
    ON dbo.TICKET (NIF_EMISOR, SERIE_FACTURA_NORM, NUM_FACTURA, FECHA_EXPEDICION)
    WHERE NIF_EMISOR IS NOT NULL
      AND NUM_FACTURA IS NOT NULL
      AND FECHA_EXPEDICION IS NOT NULL;
GO

/* -------------------------------------------------------------------------- */
/* Candidatos futuros — NO crear en el arranque                               */
/* -------------------------------------------------------------------------- */

-- CREATE NONCLUSTERED INDEX IX_TICKET_TIENDA_TPV_FECHA
--     ON dbo.TICKET (TIENDA, TPV, FECHA_EXPEDICION);
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_VENDEDOR_FECHA
--     ON dbo.TICKET (N_VENDEDOR, FECHA_EXPEDICION);
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_SERIE_NUM
--     ON dbo.TICKET (SERIE_FACTURA, NUM_FACTURA);
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_ORIGEN
--     ON dbo.TICKET (ID_RECEPCION_ORIGEN);
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_DETALLE_FAMILIA
--     ON dbo.TICKET_DETALLE (FAMILIA)
--     WHERE FAMILIA IS NOT NULL;
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_NOMBRE
--     ON dbo.TICKET_RECEPCION (NOMBRE_FICHERO, FECHA_RECEPCION);
-- GO

-- CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_ID_TICKET
--     ON dbo.TICKET_RECEPCION (ID_TICKET)
--     WHERE ID_TICKET IS NOT NULL;
-- GO
