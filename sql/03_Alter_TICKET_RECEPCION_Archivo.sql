/*
    DbInda — ALTER de TICKET_RECEPCION: archivo durable.

    IMPORTANTE:
    - Ejecutar MANUALMENTE en SSMS sobre la base existente (DbInda).
    - La aplicación .NET NO debe ejecutarlo.
    - No es un script de arranque diario: una vez basta.
    - Idempotente: se puede reejecutar si un paso ya está aplicado.

    ESTADO              = resultado de importación.
    ESTADO_ARCHIVO      = estado exclusivamente físico del XML.
    ERROR_SQL / PENDIENTE / PROCESANDO de importación NO se archivan:
    quedan con ESTADO_ARCHIVO = PENDIENTE y el XML en Entrada.

    Backfill: si RUTA_FINAL ya tiene valor, esa recepción se marca ARCHIVADO
    antes de crear el CHECK de coherencia.

    Volúmenes:
    Las rutas previstas (C:\DbInda\Entrada, Procesados, Errores) están en el
    mismo volumen: File.Move es atómico a efectos de origen (o está o no está).

    Si el destino estuviera en OTRO volumen, el movimiento es copy+delete.
    La reconciliación NO borra el origen cuando origen y destino existen
    (caso C), para no perder una posible nueva llegada física. Un leftover
    de copy+delete puede entonces generar un DUPLICADO extra; es aceptable.
    No hay heurística que borre el origen en ese caso.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('dbo.TICKET_RECEPCION', 'RUTA_DESTINO_PREVISTA') IS NULL
BEGIN
    ALTER TABLE dbo.TICKET_RECEPCION
        ADD RUTA_DESTINO_PREVISTA NVARCHAR(1024) NULL;
END
GO

IF COL_LENGTH('dbo.TICKET_RECEPCION', 'ESTADO_ARCHIVO') IS NULL
BEGIN
    ALTER TABLE dbo.TICKET_RECEPCION
        ADD ESTADO_ARCHIVO VARCHAR(20) NOT NULL
            CONSTRAINT DF_TICKET_RECEPCION_ESTADO_ARCHIVO DEFAULT ('PENDIENTE');
END
GO

UPDATE dbo.TICKET_RECEPCION
    SET ESTADO_ARCHIVO = 'ARCHIVADO'
    WHERE RUTA_FINAL IS NOT NULL
      AND ESTADO_ARCHIVO = 'PENDIENTE';
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_TICKET_RECEPCION_ESTADO_ARCHIVO'
      AND parent_object_id = OBJECT_ID(N'dbo.TICKET_RECEPCION')
)
BEGIN
    ALTER TABLE dbo.TICKET_RECEPCION
        ADD CONSTRAINT CK_TICKET_RECEPCION_ESTADO_ARCHIVO
            CHECK (ESTADO_ARCHIVO IN (
                'PENDIENTE',
                'ARCHIVANDO',
                'ARCHIVADO',
                'ERROR_ARCHIVO'
            ));
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_TICKET_RECEPCION_ARCHIVO_COHERENTE'
      AND parent_object_id = OBJECT_ID(N'dbo.TICKET_RECEPCION')
)
BEGIN
    ALTER TABLE dbo.TICKET_RECEPCION
        ADD CONSTRAINT CK_TICKET_RECEPCION_ARCHIVO_COHERENTE
            CHECK (
                (ESTADO_ARCHIVO = 'PENDIENTE' AND RUTA_FINAL IS NULL)
                OR (ESTADO_ARCHIVO = 'ARCHIVANDO'
                    AND RUTA_DESTINO_PREVISTA IS NOT NULL
                    AND RUTA_FINAL IS NULL)
                OR (ESTADO_ARCHIVO = 'ARCHIVADO' AND RUTA_FINAL IS NOT NULL)
                OR (ESTADO_ARCHIVO = 'ERROR_ARCHIVO')
            );
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_TICKET_RECEPCION_ARCHIVO_PENDIENTE'
      AND object_id = OBJECT_ID(N'dbo.TICKET_RECEPCION')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TICKET_RECEPCION_ARCHIVO_PENDIENTE
        ON dbo.TICKET_RECEPCION (ESTADO_ARCHIVO)
        WHERE ESTADO_ARCHIVO IN ('PENDIENTE', 'ARCHIVANDO', 'ERROR_ARCHIVO');
END
GO
