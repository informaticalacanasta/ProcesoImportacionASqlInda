/*
    DbInda — creación de tablas del importer TicketBAI.

    IMPORTANTE:
    - Este script es para revisión y ejecución MANUAL.
    - La aplicación .NET NO debe ejecutarlo ni migrar el esquema.
    - No ejecutar CREATE/ALTER/DROP desde el servicio.

    Base prevista: DbInda

    Identidad de factura (venta): PENDIENTE.
        No crear todavía UX_TICKET_IDENTIDAD_FACTURA.
        Falta comprobar con XML de varios días del mismo TPV si NumFactura
        se reinicia cada día (entonces entra FECHA_EXPEDICION) o es continua
        (entonces NIF + SERIE + NUMERO basta).
        El modelo conserva NIF, SERIE, SERIE_FACTURA_NORM, NUM_FACTURA y
        FECHA_EXPEDICION para cualquiera de las dos opciones.

    Duplicado de transporte (mismo fichero byte a byte):
        TICKET.HASH_SHA256 UNIQUE

    Los estados de recepción y calidad son valores propios de este importer.
    Los CHECK de esos estados evitan strings mágicos en base de datos.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* -------------------------------------------------------------------------- */
/* TICKET_RECEPCION                                                           */
/* Una fila por cada llegada de fichero, exista o no TICKET.                  */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_RECEPCION
(
    ID_RECEPCION               BIGINT         NOT NULL IDENTITY(1, 1),
    FECHA_RECEPCION            DATETIME2(3)   NOT NULL,
    NOMBRE_FICHERO             NVARCHAR(300)  NOT NULL,
    RUTA_ORIGEN                NVARCHAR(1024) NOT NULL,
    RUTA_FINAL                 NVARCHAR(1024) NULL,
    RUTA_DESTINO_PREVISTA      NVARCHAR(1024) NULL,
    ESTADO_ARCHIVO             VARCHAR(20)    NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_ESTADO_ARCHIVO DEFAULT ('PENDIENTE'),
    HASH_SHA256                CHAR(64)       NULL,
    TAMANO_BYTES               BIGINT         NULL,
    ESTADO                     VARCHAR(32)    NOT NULL,
    NUMERO_INTENTO             INT            NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_NUMERO_INTENTO DEFAULT (1),
    FECHA_PRIMER_INTENTO       DATETIME2(3)   NULL,
    FECHA_ULTIMO_INTENTO       DATETIME2(3)   NULL,
    FECHA_PROCESADO            DATETIME2(3)   NULL,
    ID_TICKET                  BIGINT         NULL,
    ES_DUPLICADO               BIT            NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_ES_DUPLICADO DEFAULT (0),
    ES_CONFLICTO_MISMA_FACTURA BIT            NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_ES_CONFLICTO DEFAULT (0),
    ID_RECEPCION_ORIGINAL      BIGINT         NULL,
    HASH_TICKET_ASOCIADO       CHAR(64)       NULL,
    XSD_VALIDO                 BIT            NULL,
    ESTADO_VALIDACION_XSD      VARCHAR(40)    NULL,
    NUMERO_WARNINGS            INT            NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_NUMERO_WARNINGS DEFAULT (0),
    NUMERO_ERRORES             INT            NOT NULL
        CONSTRAINT DF_TICKET_RECEPCION_NUMERO_ERRORES DEFAULT (0),
    MENSAJE_ERROR              NVARCHAR(MAX)  NULL,
    DETALLE_ADVERTENCIAS       NVARCHAR(MAX)  NULL,
    DETALLE_XSD                NVARCHAR(MAX)  NULL,
    NOMBRE_NIF_FICHERO         VARCHAR(9)     NULL,
    SERIE_FICHERO              VARCHAR(20)    NULL,
    TIENDA_FICHERO             INT            NULL,
    TPV_FICHERO                INT            NULL,
    NUM_FACTURA_FICHERO        VARCHAR(20)    NULL,
    FECHA_FICHERO              DATE           NULL,
    HORA_FICHERO               TIME(0)        NULL,
    IMPORTE_FICHERO            DECIMAL(14, 2) NULL,

    CONSTRAINT PK_TICKET_RECEPCION
        PRIMARY KEY CLUSTERED (ID_RECEPCION),

    CONSTRAINT CK_TICKET_RECEPCION_ESTADO
        CHECK (ESTADO IN (
            'PENDIENTE',
            'PROCESANDO',
            'PROCESADO',
            'PROCESADO_CON_ADVERTENCIAS',
            'DUPLICADO',
            'CONFLICTO_MISMA_FACTURA',
            'ERROR_TEMPORAL',
            'ERROR_XML',
            'ERROR_SQL',
            'ERROR_PERMANENTE'
        )),

    CONSTRAINT CK_TICKET_RECEPCION_TAMANO
        CHECK (TAMANO_BYTES IS NULL OR TAMANO_BYTES >= 0),

    CONSTRAINT CK_TICKET_RECEPCION_INTENTO
        CHECK (NUMERO_INTENTO >= 1),

    CONSTRAINT CK_TICKET_RECEPCION_HASH
        CHECK (HASH_SHA256 IS NULL OR (LEN(HASH_SHA256) = 64 AND HASH_SHA256 NOT LIKE '%[^0-9A-Fa-f]%')),

    CONSTRAINT CK_TICKET_RECEPCION_HASH_ASOCIADO
        CHECK (HASH_TICKET_ASOCIADO IS NULL OR (LEN(HASH_TICKET_ASOCIADO) = 64 AND HASH_TICKET_ASOCIADO NOT LIKE '%[^0-9A-Fa-f]%')),

    CONSTRAINT CK_TICKET_RECEPCION_ESTADO_VALIDACION_XSD
        CHECK (ESTADO_VALIDACION_XSD IS NULL OR ESTADO_VALIDACION_XSD IN (
            'VALIDO',
            'INVALIDO_INCOMPATIBILIDAD_CONOCIDA',
            'INVALIDO_DATOS',
            'NO_VALIDABLE'
        )),

    CONSTRAINT CK_TICKET_RECEPCION_XSD_COHERENTE
        CHECK (
            (XSD_VALIDO IS NULL AND (ESTADO_VALIDACION_XSD IS NULL OR ESTADO_VALIDACION_XSD = 'NO_VALIDABLE'))
            OR (XSD_VALIDO = 1 AND ESTADO_VALIDACION_XSD = 'VALIDO')
            OR (XSD_VALIDO = 0 AND ESTADO_VALIDACION_XSD IN (
                    'INVALIDO_INCOMPATIBILIDAD_CONOCIDA',
                    'INVALIDO_DATOS'
                ))
        ),

    CONSTRAINT CK_TICKET_RECEPCION_ESTADO_ARCHIVO
        CHECK (ESTADO_ARCHIVO IN (
            'PENDIENTE',
            'ARCHIVANDO',
            'ARCHIVADO',
            'ERROR_ARCHIVO'
        )),

    CONSTRAINT CK_TICKET_RECEPCION_ARCHIVO_COHERENTE
        CHECK (
            (ESTADO_ARCHIVO = 'PENDIENTE' AND RUTA_FINAL IS NULL)
            OR (ESTADO_ARCHIVO = 'ARCHIVANDO'
                AND RUTA_DESTINO_PREVISTA IS NOT NULL
                AND RUTA_FINAL IS NULL)
            OR (ESTADO_ARCHIVO = 'ARCHIVADO' AND RUTA_FINAL IS NOT NULL)
            OR (ESTADO_ARCHIVO = 'ERROR_ARCHIVO')
        )
);
GO

/*
    HASH_SHA256 es NULL si el fichero no pudo leerse.
    HASH_TICKET_ASOCIADO guarda el hash del TICKET ya existente en
    DUPLICADO / CONFLICTO_MISMA_FACTURA para ver la diferencia sin join.

    XSD_VALIDO refleja el resultado técnico literal del validador:
    - 1    = el XML cumple el XSD (cero errores de schema).
    - 0    = el XSD produjo uno o más errores.
    - NULL = no pudo realizarse la validación.

    ESTADO_VALIDACION_XSD clasifica ese resultado para el importer:
    - VALIDO
          XSD_VALIDO = 1.
    - INVALIDO_INCOMPATIBILIDAD_CONOCIDA
          XSD_VALIDO = 0, pero todos los errores son desajustes sistemáticos
          del esquema usado (Signature ausente, PvpConsumo/PVPConsumo,
          schemaLocation/import). No es advertencia de negocio.
          El ticket NO queda CON_ADVERTENCIAS solo por esto.
    - INVALIDO_DATOS
          XSD_VALIDO = 0 y hay errores XSD ajenos a esas incompatibilidades.
          Si además hay incompatibilidad conocida, gana INVALIDO_DATOS.
    - NO_VALIDABLE
          XSD_VALIDO = NULL (sin XSD, XML ilegible antes de validar, etc.).

    DETALLE_XSD: todos los eventos técnicos, incluidos los conocidos.
    DETALLE_ADVERTENCIAS: solo problemas reales de dato/negocio.
    NUMERO_WARNINGS cuenta únicamente DETALLE_ADVERTENCIAS.

    Metadatos *_FICHERO: parseo del nombre. No son la fuente de verdad.

    ESTADO = resultado de la importación SQL (PENDIENTE, PROCESADO, ERROR_SQL, …).
    ESTADO_ARCHIVO = estado exclusivamente físico del XML en disco.
    No se archiva mientras ESTADO sea ERROR_SQL, PENDIENTE o PROCESANDO:
    el fichero permanece en Entrada y ESTADO_ARCHIVO sigue PENDIENTE.

    RUTA_DESTINO_PREVISTA se rellena al pasar a ARCHIVANDO y se vacía al ARCHIVADO.
    Reconciliación SQL→disco (mismo volumen, caso previsto C:\DbInda\*):
      A origen sí, destino no     → mover a la prevista
      B origen no, destino hash OK → ARCHIVADO (Move OK + UPDATE fallido)
      C origen sí, destino hash OK → ARCHIVADO usando el destino; NO borrar el origen
                                    (posible nueva llegada física)
      D ninguno                    → ERROR_ARCHIVO
    Volúmenes distintos (copy+delete): la misma regla C no borra el origen.
    Un leftover de copia incompleta puede generar un DUPLICADO extra; es preferible
    a borrar una llegada física real. No hay heurística que borre el origen en C.
*/

ALTER TABLE dbo.TICKET_RECEPCION
    ADD CONSTRAINT FK_TICKET_RECEPCION_ORIGINAL
        FOREIGN KEY (ID_RECEPCION_ORIGINAL)
        REFERENCES dbo.TICKET_RECEPCION (ID_RECEPCION)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
GO

/* -------------------------------------------------------------------------- */
/* TICKET                                                                     */
/* Una venta/factura. No se inserta en DUPLICADO ni CONFLICTO_MISMA_FACTURA.  */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET
(
    ID_TICKET                         BIGINT         NOT NULL IDENTITY(1, 1),
    ID_RECEPCION_ORIGEN               BIGINT         NOT NULL,
    HASH_SHA256                       CHAR(64)       NOT NULL,
    FECHA_ALTA                        DATETIME2(3)   NOT NULL,
    ESTADO_CALIDAD                    VARCHAR(32)    NOT NULL,

    NIF_EMISOR                        CHAR(9)        NULL,
    RAZON_SOCIAL_EMISOR               NVARCHAR(120)  NULL,
    SERIE_FACTURA                     VARCHAR(20)    NULL,
    SERIE_FACTURA_NORM                AS CAST(ISNULL(SERIE_FACTURA, '') AS VARCHAR(20)) PERSISTED,
    NUM_FACTURA                       VARCHAR(20)    NULL,
    FECHA_EXPEDICION                  DATE           NULL,
    HORA_EXPEDICION                   TIME(0)        NULL,
    FECHA_HORA_EXPEDICION             AS (
                                            IIF(
                                                FECHA_EXPEDICION IS NULL OR HORA_EXPEDICION IS NULL,
                                                NULL,
                                                DATETIME2FROMPARTS(
                                                    YEAR(FECHA_EXPEDICION),
                                                    MONTH(FECHA_EXPEDICION),
                                                    DAY(FECHA_EXPEDICION),
                                                    DATEPART(HOUR, HORA_EXPEDICION),
                                                    DATEPART(MINUTE, HORA_EXPEDICION),
                                                    DATEPART(SECOND, HORA_EXPEDICION),
                                                    0,
                                                    0
                                                )
                                            )
                                        ) PERSISTED,
    TIENDA                            INT            NULL,
    TPV                               INT            NULL,
    N_VENDEDOR                        INT            NULL,
    D_VENDEDOR                        NVARCHAR(40)   NULL,
    N_FORMA_PAGO                      INT            NULL,
    D_FORMA_PAGO                      NVARCHAR(30)   NULL,
    IMPORTE_TOTAL                     DECIMAL(14, 2) NULL,
    FACTURA_SIMPLIFICADA              BIT            NULL,
    FACTURA_SUSTITUCION_SIMPLIFICADA  BIT            NULL,
    EMITIDA_POR                       VARCHAR(1)     NULL,
    DESCRIPCION_FACTURA               NVARCHAR(250)  NULL,
    FECHA_OPERACION                   DATE           NULL,
    RETENCION_SOPORTADA               DECIMAL(14, 2) NULL,
    BASE_IMPONIBLE_A_COSTE            DECIMAL(14, 2) NULL,
    ID_VERSION_TBAI                   VARCHAR(10)    NULL,
    N_ENCARGO                         BIGINT         NULL,
    ID_SALA                           INT            NULL,
    TIPO_MESA                         NVARCHAR(10)   NULL,
    ID_MESA                           INT            NULL,
    ID_CLIENT                         NVARCHAR(30)   NULL,
    NUM_SERIE_DISPOSITIVO             VARCHAR(30)    NULL,
    SERIE_FACTURA_ANTERIOR            VARCHAR(20)    NULL,
    NUM_FACTURA_ANTERIOR              VARCHAR(20)    NULL,
    FECHA_FACTURA_ANTERIOR            DATE           NULL,
    HASH_FIRMA_FACTURA_ANTERIOR       VARCHAR(128)   NULL,
    NUMERO_WARNINGS                   INT            NOT NULL
        CONSTRAINT DF_TICKET_NUMERO_WARNINGS DEFAULT (0),

    CONSTRAINT PK_TICKET
        PRIMARY KEY CLUSTERED (ID_TICKET),

    CONSTRAINT FK_TICKET_RECEPCION_ORIGEN
        FOREIGN KEY (ID_RECEPCION_ORIGEN)
        REFERENCES dbo.TICKET_RECEPCION (ID_RECEPCION)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_HASH_SHA256
        UNIQUE NONCLUSTERED (HASH_SHA256),

    CONSTRAINT CK_TICKET_ESTADO_CALIDAD
        CHECK (ESTADO_CALIDAD IN ('OK', 'CON_ADVERTENCIAS', 'INCOMPLETO')),

    CONSTRAINT CK_TICKET_HASH
        CHECK (LEN(HASH_SHA256) = 64 AND HASH_SHA256 NOT LIKE '%[^0-9A-Fa-f]%'),

    CONSTRAINT CK_TICKET_NIF_EMISOR
        CHECK (NIF_EMISOR IS NULL OR LEN(NIF_EMISOR) = 9)
);
GO

/*
    NULL justificado:
    - Cualquier dato fiscal/operativo puede faltar o ser inconvertible.
      El ticket incompleto debe poder persistirse.
    - SERIE_FACTURA es opcional en el XSD (minOccurs=0).
    - NUM_SERIE_DISPOSITIVO es opcional en HuellaTBAI.

    NOT NULL:
    - ID_RECEPCION_ORIGEN: toda venta nace de una recepción concreta.
    - HASH_SHA256: identidad de transporte; UNIQUE impide dos ventas
      del mismo XML byte a byte.
    - ESTADO_CALIDAD: siempre hay clasificación.

    Relación TICKET / TICKET_RECEPCION (la aplicación mantiene la coherencia):
    - Recepción que origina una venta:
          TICKET.ID_RECEPCION_ORIGEN = esa recepción.
          TICKET_RECEPCION.ID_TICKET = el ticket creado.
    - Recepción DUPLICADO:
          no crea TICKET.
          TICKET_RECEPCION.ID_TICKET = ticket ya existente.
    - Recepción CONFLICTO_MISMA_FACTURA:
          no crea TICKET.
          TICKET_RECEPCION.ID_TICKET = ticket ya existente.
    Las dos columnas conviven porque no significan lo mismo:
    ID_RECEPCION_ORIGEN apunta solo a la recepción que creó la venta;
    ID_TICKET en recepción apunta al ticket asociado, también en
    retransmisiones y conflictos.

    SERIE_FACTURA_NORM:
    - Preparada para la UNIQUE de identidad, todavía PENDIENTE.
    - NULL de serie se trata como '' cuando exista esa constraint.
    - No usar esta columna en consultas de negocio.

    TIENDA / TPV:
    - Extraídos de SERIE_FACTURA cuando el patrón n.n.* es numérico
      (ejemplo real: 52.2.1 → tienda 52, TPV 2).
    - NULL si no puede determinarse con seguridad.

    NUM_SERIE_DISPOSITIVO:
    - No forma parte del encadenamiento TicketBAI.
    - Identifica el hardware/TPV que emitió el XML (TextMax30Type).
    - En la muestra de feria los 430 ficheros comparten el mismo valor.
      Con varias tiendas/TPV en la misma carpeta permite agrupar por dispositivo.

    EncadenamientoFacturaAnterior (HuellaTBAI, opcional):
    - SERIE_FACTURA_ANTERIOR se guarda íntegra (p. ej. 66.1.1).
      No se parte en tienda/TPV/serie: no hay columnas TIENDA_ANTERIOR/TPV_ANTERIOR.
    - NUM_FACTURA_ANTERIOR, FECHA_FACTURA_ANTERIOR, HASH_FIRMA_FACTURA_ANTERIOR
      (SignatureValueFirmaFacturaAnterior).
    - Si el bloque no existe, las cuatro columnas quedan NULL.

    No se almacenan:
    - Software/Nombre, LicenciaTBAI, Version (no se consultarán en análisis de ventas).
    - Campos DirE (no aparecen en los XML reales).
*/

/*
    PENDIENTE — UX_TICKET_IDENTIDAD_FACTURA

    No crear hasta comprobar XML de varios días consecutivos del mismo TPV.

    Opción A (NumFactura se reinicia cada día):
        UNIQUE (NIF_EMISOR, SERIE_FACTURA_NORM, NUM_FACTURA, FECHA_EXPEDICION)
        WHERE NIF_EMISOR IS NOT NULL AND NUM_FACTURA IS NOT NULL
          AND FECHA_EXPEDICION IS NOT NULL

    Opción B (la numeración continúa entre días):
        UNIQUE (NIF_EMISOR, SERIE_FACTURA_NORM, NUM_FACTURA)
        WHERE NIF_EMISOR IS NOT NULL AND NUM_FACTURA IS NOT NULL

    La aplicación puede detectar CONFLICTO_MISMA_FACTURA por consulta
    mientras esta constraint no exista; la UNIQUE solo se añadirá después
    para proteger también las condiciones de carrera.

    LIMITACIÓN CONOCIDA (desarrollo, no producción):
    Sin UX_TICKET_IDENTIDAD_FACTURA, dos workers concurrentes con la misma
    identidad empresarial (NIF + serie + número [+ fecha]) y HASH distintos
    pueden superar ambos el SELECT previo e insertar dos TICKET.
    No se usa SERIALIZABLE ni locks extra. El duplicado exacto de bytes sí
    está cubierto: UX_TICKET_HASH_SHA256 + tratamiento de carrera 2627/2601.
    No poner en producción hasta resolver la UNIQUE de identidad con XML
    de varios días del mismo TPV.
*/

ALTER TABLE dbo.TICKET_RECEPCION
    ADD CONSTRAINT FK_TICKET_RECEPCION_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION;
GO

/* -------------------------------------------------------------------------- */
/* TICKET_DETALLE                                                             */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_DETALLE
(
    ID_TICKET_DETALLE    BIGINT          NOT NULL IDENTITY(1, 1),
    ID_TICKET            BIGINT          NOT NULL,
    NUM_LINEA            INT             NOT NULL,
    DESCRIPCION          NVARCHAR(250)   NULL,
    CANTIDAD             DECIMAL(15, 3)  NULL,
    IMPORTE_UNITARIO     DECIMAL(20, 8)  NULL,
    DESCUENTO            DECIMAL(14, 2)  NULL,
    IMPORTE_TOTAL        DECIMAL(14, 2)  NULL,
    CODIGO_CENTRAL       VARCHAR(13)     NULL,
    IDENTIFICADOR        VARCHAR(13)     NULL,
    FAMILIA              VARCHAR(13)     NULL,
    SECCION              INT             NULL,
    FORMATO              INT             NULL,
    ESPERPES             BIT             NULL,
    SECCION_SALA         NVARCHAR(20)    NULL,
    PVP_CONSUMO          DECIMAL(14, 2)  NULL,
    ES_KIT               BIT             NULL,
    ID_TIQUETL_MASTER    VARCHAR(50)     NULL,
    ID_TIQUETL           VARCHAR(50)     NULL,
    EQUIVALENCIA_UNIDAD  DECIMAL(20, 8)  NULL,
    EQUIVALENCIA_PESO    DECIMAL(14, 2)  NULL,
    PORCENTAJE_IVA       DECIMAL(14, 2)  NULL,
    PORCENTAJE_RECARGO   DECIMAL(14, 2)  NULL,

    CONSTRAINT PK_TICKET_DETALLE
        PRIMARY KEY CLUSTERED (ID_TICKET_DETALLE),

    CONSTRAINT FK_TICKET_DETALLE_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_DETALLE_LINEA
        UNIQUE NONCLUSTERED (ID_TICKET, NUM_LINEA),

    CONSTRAINT CK_TICKET_DETALLE_NUM_LINEA
        CHECK (NUM_LINEA >= 1)
);
GO

/*
    Precisión según XSD:
    - Cantidad          ImporteSgn12.3  → DECIMAL(15, 3)
    - ImporteUnitario   ImporteSgn12.8  → DECIMAL(20, 8)
    - Importes 12.2                     → DECIMAL(14, 2)
    - CodigoCentral / Identificador / Familia  TextMax13Type
    - Id_tiquetl / Id_tiquetlmaster     TextMax50Type

    IMPORTE_UNITARIO = 0.00000000 es un valor real del TPV, no un hueco.
    Se guarda 0. No se reconstruye. No genera advertencia.

    ESPERPES / ES_KIT: S/N del XSD → BIT. Cualquier otro valor → NULL.
    PVP_CONSUMO: el XML real usa PvpConsumo; el XSD declara PVPConsumo.
    El parser leerá ambos nombres bajo IDDetalleFactura.
*/

/* -------------------------------------------------------------------------- */
/* TICKET_IVA                                                                 */
/* Un bloque fiscal por fila. No aplanar tipos de IVA en TICKET.              */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_IVA
(
    ID_TICKET_IVA                        BIGINT         NOT NULL IDENTITY(1, 1),
    ID_TICKET                            BIGINT         NOT NULL,
    NUM_ORDEN                            INT            NOT NULL,
    TIPO_DESGLOSE                        VARCHAR(32)    NULL,
    TIPO_SUJECION                        VARCHAR(16)    NULL,
    TIPO_NO_EXENTA                       VARCHAR(2)     NULL,
    CAUSA_EXENCION                       VARCHAR(2)     NULL,
    CAUSA_NO_SUJETA                      VARCHAR(2)     NULL,
    BASE_IMPONIBLE                       DECIMAL(14, 2) NULL,
    TIPO_IMPOSITIVO                      DECIMAL(5, 2)  NULL,
    CUOTA_IMPUESTO                       DECIMAL(14, 2) NULL,
    TIPO_RECARGO_EQUIVALENCIA            DECIMAL(5, 2)  NULL,
    CUOTA_RECARGO_EQUIVALENCIA           DECIMAL(14, 2) NULL,
    OPERACION_RECARGO_O_SIMPLIFICADO     BIT            NULL,
    IMPORTE_NO_SUJETA                    DECIMAL(14, 2) NULL,

    CONSTRAINT PK_TICKET_IVA
        PRIMARY KEY CLUSTERED (ID_TICKET_IVA),

    CONSTRAINT FK_TICKET_IVA_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_IVA_ORDEN
        UNIQUE NONCLUSTERED (ID_TICKET, NUM_ORDEN),

    CONSTRAINT CK_TICKET_IVA_NUM_ORDEN
        CHECK (NUM_ORDEN >= 1)
);
GO

/*
    TIPO_DESGLOSE: DesgloseFactura | PrestacionServicios | Entrega
    TIPO_SUJECION: NoExenta | Exenta | NoSujeta
    TIPO_NO_EXENTA: S1 / S2
    CAUSA_EXENCION: E1-E6
    CAUSA_NO_SUJETA: OT / RL
    TIPO_IMPOSITIVO: Tipo3.2Type → DECIMAL(5, 2)

    La muestra real solo trae NoExenta/S1 con tipos 0, 4, 10 y 21.
    El modelo cubre Exenta, NoSujeta y DesgloseTipoOperacion del XSD.
*/

/* -------------------------------------------------------------------------- */
/* TICKET_CLAVE                                                               */
/* Hasta 3 ClaveRegimenIvaOpTrascendencia por factura (XSD maxOccurs=3).      */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_CLAVE
(
    ID_TICKET_CLAVE     BIGINT      NOT NULL IDENTITY(1, 1),
    ID_TICKET           BIGINT      NOT NULL,
    NUM_ORDEN           TINYINT     NOT NULL,
    CLAVE_REGIMEN_IVA   VARCHAR(2)  NOT NULL,

    CONSTRAINT PK_TICKET_CLAVE
        PRIMARY KEY CLUSTERED (ID_TICKET_CLAVE),

    CONSTRAINT FK_TICKET_CLAVE_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_CLAVE_ORDEN
        UNIQUE NONCLUSTERED (ID_TICKET, NUM_ORDEN),

    CONSTRAINT CK_TICKET_CLAVE_NUM_ORDEN
        CHECK (NUM_ORDEN BETWEEN 1 AND 3),

    CONSTRAINT CK_TICKET_CLAVE_VALOR
        CHECK (LEN(CLAVE_REGIMEN_IVA) = 2)
);
GO

/*
    VARCHAR(2): el XSD enumera códigos de exactamente 2 caracteres (01, 02, …, 53).
    Si llega un valor inválido o de otra longitud: no insertar la fila, NULL no
    aplica aquí (la columna es NOT NULL); el parser omite la clave y registra
    advertencia de dato. No truncar.
    En los 430 XML hay siempre exactamente 1 clave (01).
*/

/* -------------------------------------------------------------------------- */
/* TICKET_DESTINATARIO                                                        */
/* 0..100 por factura. No asumir 1:1.                                         */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_DESTINATARIO
(
    ID_TICKET_DESTINATARIO  BIGINT         NOT NULL IDENTITY(1, 1),
    ID_TICKET               BIGINT         NOT NULL,
    NUM_ORDEN               INT            NOT NULL,
    NIF                     CHAR(9)        NULL,
    CODIGO_PAIS             CHAR(2)        NULL,
    ID_TYPE                 VARCHAR(2)     NULL,
    ID_OTRO                 VARCHAR(20)    NULL,
    APELLIDOS_NOMBRE        NVARCHAR(120)  NULL,
    CODIGO_POSTAL           NVARCHAR(20)   NULL,
    DIRECCION               NVARCHAR(250)  NULL,

    CONSTRAINT PK_TICKET_DESTINATARIO
        PRIMARY KEY CLUSTERED (ID_TICKET_DESTINATARIO),

    CONSTRAINT FK_TICKET_DESTINATARIO_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_DESTINATARIO_ORDEN
        UNIQUE NONCLUSTERED (ID_TICKET, NUM_ORDEN),

    CONSTRAINT CK_TICKET_DESTINATARIO_NUM_ORDEN
        CHECK (NUM_ORDEN >= 1)
);
GO

/*
    NIF e IDOtro son un choice en el XSD: uno u otro.
    Ambos NULL si el bloque está incompleto.
    Ninguno de los 430 XML de feria trae destinatarios.
*/

/* -------------------------------------------------------------------------- */
/* TICKET_RECTIFICACION                                                       */
/* PROVISIONAL. Ninguno de los 430 XML actuales es rectificativo.             */
/* La estructura se revisará con un XML rectificativo real.                   */
/* No ampliar este diseño hasta entonces.                                     */
/* -------------------------------------------------------------------------- */

CREATE TABLE dbo.TICKET_RECTIFICACION
(
    ID_TICKET_RECTIFICACION     BIGINT         NOT NULL IDENTITY(1, 1),
    ID_TICKET                   BIGINT         NOT NULL,
    NUM_ORDEN                   INT            NOT NULL,
    CODIGO                      VARCHAR(2)     NULL,
    TIPO                        CHAR(1)        NULL,
    BASE_RECTIFICADA            DECIMAL(14, 2) NULL,
    CUOTA_RECTIFICADA           DECIMAL(14, 2) NULL,
    CUOTA_RECARGO_RECTIFICADA   DECIMAL(14, 2) NULL,
    SERIE_FACTURA               VARCHAR(20)    NULL,
    NUM_FACTURA                 VARCHAR(20)    NULL,
    FECHA_EXPEDICION            DATE           NULL,

    CONSTRAINT PK_TICKET_RECTIFICACION
        PRIMARY KEY CLUSTERED (ID_TICKET_RECTIFICACION),

    CONSTRAINT FK_TICKET_RECTIFICACION_TICKET
        FOREIGN KEY (ID_TICKET)
        REFERENCES dbo.TICKET (ID_TICKET)
        ON DELETE NO ACTION
        ON UPDATE NO ACTION,

    CONSTRAINT UX_TICKET_RECTIFICACION_ORDEN
        UNIQUE NONCLUSTERED (ID_TICKET, NUM_ORDEN),

    CONSTRAINT CK_TICKET_RECTIFICACION_NUM_ORDEN
        CHECK (NUM_ORDEN >= 1)
);
GO

/*
    PROVISIONAL — revisar con XML rectificativo real.

    Borrador actual según XSD:
    - FacturaRectificativa (0..1): CODIGO (R1-R5), TIPO (S/I), importes.
    - FacturasRectificadasSustituidas (0..100): SERIE/NUM/FECHA referenciadas.
    - Cabecera sin facturas referenciadas: una fila con referencias NULL.
    - N facturas referenciadas: N filas, copiando CODIGO/TIPO/importes.

    No confundir con EncadenamientoFacturaAnterior (se persiste en TICKET, no aquí).
    No añadir más tablas de rectificación hasta ver XML reales.
*/
GO
