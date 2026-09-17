/*
    TicketsTPV — login y usuario de aplicación para el Worker.

    IMPORTANTE:
    - Ejecución MANUAL, como administrador SQL (sa u otro sysadmin).
    - La aplicación .NET NO debe ejecutarlo.
    - No usar sa desde el Worker.
    - No conceder db_owner.
    - Sustituir <SET_STRONG_PASSWORD_HERE> por una contraseña fuerte
      ANTES de ejecutar. No dejar el placeholder en producción.
    - La clave de configuración de la app sigue siendo ConnectionStrings:DbInda;
      este script solo crea el login SQL ticketstpv_app sobre la base TicketsTPV.

    Permisos (superficie real del Worker):
    - TICKET_RECEPCION: SELECT, INSERT, UPDATE
    - TICKET y tablas hijas: SELECT, INSERT
    - Sin DELETE, sin EXECUTE (no hay procedimientos), sin ALTER.

    Idempotente: se puede reejecutar. CREATE LOGIN / CREATE USER se omiten
    si ya existen. GRANT se puede repetir.
*/

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'ticketstpv_app')
BEGIN
    CREATE LOGIN [ticketstpv_app]
        WITH PASSWORD = N'<SET_STRONG_PASSWORD_HERE>',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
END
GO

USE [TicketsTPV];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'ticketstpv_app')
BEGIN
    CREATE USER [ticketstpv_app]
        FOR LOGIN [ticketstpv_app]
        WITH DEFAULT_SCHEMA = [dbo];
END
GO

GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.TICKET_RECEPCION TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET_DETALLE TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET_IVA TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET_CLAVE TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET_DESTINATARIO TO [ticketstpv_app];
GRANT SELECT, INSERT ON OBJECT::dbo.TICKET_RECTIFICACION TO [ticketstpv_app];
GO
