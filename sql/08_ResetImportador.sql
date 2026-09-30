/*
  Vaciado de datos del importador para comenzar desde cero.
  Ejecutar primero con @Ejecutar = 0 para revisar servidor, tablas y recuentos.
  Tras comprobar una copia de seguridad de SQL Server, cambiar a 1 y ejecutar.
  No elimina tablas, usuarios, permisos ni reinicia los contadores IDENTITY.
*/
USE [TicketsTPV];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Ejecutar bit = 0;
DECLARE @Tablas table (Nombre sysname NOT NULL PRIMARY KEY);
INSERT INTO @Tablas (Nombre) VALUES
    (N'TICKET_RECEPCION'), (N'TICKET'), (N'TICKET_DETALLE'),
    (N'TICKET_IVA'), (N'TICKET_CLAVE'), (N'TICKET_DESTINATARIO'),
    (N'TICKET_RECTIFICACION'), (N'PEDIDO_TPV_RECEPCION'),
    (N'PEDIDO_TPV'), (N'PEDIDO_TPV_DETALLE'),
    (N'IMPORTADOR_EJECUCION'), (N'IMPORTADOR_INTENTO'),
    (N'IMPORTADOR_EVENTO');

SELECT @@SERVERNAME AS Servidor, DB_NAME() AS BaseDeDatos, @Ejecutar AS Ejecutar;

IF EXISTS (SELECT 1 FROM @Tablas WHERE OBJECT_ID(N'dbo.' + Nombre, N'U') IS NULL)
BEGIN
    SELECT Nombre AS TablaAusente FROM @Tablas
    WHERE OBJECT_ID(N'dbo.' + Nombre, N'U') IS NULL;
    THROW 50000, 'Faltan tablas del importador. No se ha borrado nada.', 1;
END;

-- Una tabla ajena con FK hacia estas tablas impediría un reinicio completo.
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys AS fk
    WHERE fk.referenced_object_id IN
        (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas)
      AND fk.parent_object_id NOT IN
        (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas)
)
BEGIN
    SELECT fk.name AS ClaveForanea,
           OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id) AS TablaAjena,
           OBJECT_SCHEMA_NAME(fk.referenced_object_id) + N'.' + OBJECT_NAME(fk.referenced_object_id) AS TablaImportador
    FROM sys.foreign_keys AS fk
    WHERE fk.referenced_object_id IN
        (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas)
      AND fk.parent_object_id NOT IN
        (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas);
    THROW 50001, 'Hay claves foraneas desde tablas ajenas. No se ha borrado nada.', 1;
END;

IF EXISTS (
    SELECT 1 FROM (VALUES
        (N'TICKET_RECEPCION', N'ID_TICKET'),
        (N'TICKET_RECEPCION', N'ID_RECEPCION_ORIGINAL'),
        (N'PEDIDO_TPV_RECEPCION', N'ID_PEDIDO')
    ) AS requerido(Tabla, Columna)
    LEFT JOIN sys.columns AS c
      ON c.object_id = OBJECT_ID(N'dbo.' + requerido.Tabla)
     AND c.name = requerido.Columna
    WHERE c.column_id IS NULL OR c.is_nullable = 0
)
    THROW 50002, 'Las columnas de enlace no son anulables como se esperaba. No se ha borrado nada.', 1;

IF EXISTS (
    SELECT 1 FROM sys.triggers AS tr
    WHERE tr.parent_id IN (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas)
      AND tr.is_disabled = 0
)
BEGIN
    SELECT tr.name AS TriggerActivo,
           OBJECT_SCHEMA_NAME(tr.parent_id) + N'.' + OBJECT_NAME(tr.parent_id) AS Tabla
    FROM sys.triggers AS tr
    WHERE tr.parent_id IN (SELECT OBJECT_ID(N'dbo.' + Nombre) FROM @Tablas)
      AND tr.is_disabled = 0;
    THROW 50003, 'Hay triggers activos sobre las tablas. Revisarlos antes de borrar.', 1;
END;

SELECT N'TICKET_RECEPCION' AS Tabla, COUNT_BIG(*) AS Filas FROM dbo.TICKET_RECEPCION
UNION ALL SELECT N'TICKET', COUNT_BIG(*) FROM dbo.TICKET
UNION ALL SELECT N'TICKET_DETALLE', COUNT_BIG(*) FROM dbo.TICKET_DETALLE
UNION ALL SELECT N'TICKET_IVA', COUNT_BIG(*) FROM dbo.TICKET_IVA
UNION ALL SELECT N'TICKET_CLAVE', COUNT_BIG(*) FROM dbo.TICKET_CLAVE
UNION ALL SELECT N'TICKET_DESTINATARIO', COUNT_BIG(*) FROM dbo.TICKET_DESTINATARIO
UNION ALL SELECT N'TICKET_RECTIFICACION', COUNT_BIG(*) FROM dbo.TICKET_RECTIFICACION
UNION ALL SELECT N'PEDIDO_TPV_RECEPCION', COUNT_BIG(*) FROM dbo.PEDIDO_TPV_RECEPCION
UNION ALL SELECT N'PEDIDO_TPV', COUNT_BIG(*) FROM dbo.PEDIDO_TPV
UNION ALL SELECT N'PEDIDO_TPV_DETALLE', COUNT_BIG(*) FROM dbo.PEDIDO_TPV_DETALLE
UNION ALL SELECT N'IMPORTADOR_EJECUCION', COUNT_BIG(*) FROM dbo.IMPORTADOR_EJECUCION
UNION ALL SELECT N'IMPORTADOR_INTENTO', COUNT_BIG(*) FROM dbo.IMPORTADOR_INTENTO
UNION ALL SELECT N'IMPORTADOR_EVENTO', COUNT_BIG(*) FROM dbo.IMPORTADOR_EVENTO;

IF @Ejecutar = 0
BEGIN
    PRINT N'Solo comprobacion: no se ha borrado nada. Para vaciar, establezca @Ejecutar = 1.';
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    DELETE FROM dbo.IMPORTADOR_EVENTO;
    DELETE FROM dbo.IMPORTADOR_INTENTO;
    DELETE FROM dbo.IMPORTADOR_EJECUCION;

    UPDATE dbo.PEDIDO_TPV_RECEPCION SET ID_PEDIDO = NULL WHERE ID_PEDIDO IS NOT NULL;
    DELETE FROM dbo.PEDIDO_TPV_DETALLE;
    DELETE FROM dbo.PEDIDO_TPV;
    DELETE FROM dbo.PEDIDO_TPV_RECEPCION;

    UPDATE dbo.TICKET_RECEPCION
       SET ID_TICKET = NULL, ID_RECEPCION_ORIGINAL = NULL
     WHERE ID_TICKET IS NOT NULL OR ID_RECEPCION_ORIGINAL IS NOT NULL;
    DELETE FROM dbo.TICKET_DETALLE;
    DELETE FROM dbo.TICKET_IVA;
    DELETE FROM dbo.TICKET_CLAVE;
    DELETE FROM dbo.TICKET_DESTINATARIO;
    DELETE FROM dbo.TICKET_RECTIFICACION;
    DELETE FROM dbo.TICKET;
    DELETE FROM dbo.TICKET_RECEPCION;

    COMMIT TRANSACTION;
    PRINT N'Datos del importador eliminados. Tablas y configuracion conservadas.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
