/*
    SqlLookout: a database and login to keep its history and settings in SQL Server.

    Only needed if you start SqlLookout with --store-connection-file (otherwise it keeps them in a SQLite file in its
    data folder). Run once, as a sysadmin, on the SQL Server that should hold them. Edit the lines marked EDIT.

    Use a database of its own. SqlLookout creates its tables there (Runs, Samples, Alerts, Users, Instances, ...) the
    first time it starts. The login gets db_owner on that database and nothing anywhere else.

    The history grows with the number of servers and how long it is kept (Settings > retention); plan for a few hundred MB
    per monitored server for 90 days. Put the database on a server that is not one of the monitored ones, or at least
    not on one whose outage you want to be told about by SqlLookout itself.

    The connection string file, one line (a SQL login; for Windows authentication use Integrated Security=true and run
    the SqlLookout service as an account that has a login with db_owner on the database):

        Server=STORE01;Database=SqlLookoutStore;User Id=sqllookout_store;Password=...;Encrypt=true;TrustServerCertificate=true

    Then:   sqllookout.exe install --store-connection-file C:\path\to\that-file.txt
    The installer checks the connection, saves it protected with Windows DPAPI, and you can delete the plain file.

    Works on SQL Server 2012 and later (uses ALTER ROLE ... ADD MEMBER).
    For a Windows login replace the CREATE LOGIN line with:   CREATE LOGIN [DOMAIN\account] FROM WINDOWS;
*/
USE master;
GO

DECLARE @Database sysname       = N'SqlLookoutStore';                      -- EDIT: database name
DECLARE @Name     sysname       = N'sqllookout_store';                     -- EDIT: login name
DECLARE @Password nvarchar(128) = N'CHANGE-ME-to-a-long-random-password';  -- EDIT: SQL login only

DECLARE @sql nvarchar(max);

IF DB_ID(@Database) IS NULL
BEGIN
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@Database) + N';';
    EXEC (@sql);
END

IF SUSER_ID(@Name) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@Name) + N' WITH PASSWORD = N''' + REPLACE(@Password, N'''', N'''''')
             + N''', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(@Database) + N';';
    EXEC (@sql);
END

-- Inside the new database: a user for the login, and db_owner (the program creates and updates its tables).
SET @sql = N'USE ' + QUOTENAME(@Database) + N';
IF USER_ID(' + QUOTENAME(@Name, '''') + N') IS NULL CREATE USER ' + QUOTENAME(@Name) + N' FOR LOGIN ' + QUOTENAME(@Name) + N';
ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@Name) + N';';
EXEC (@sql);

PRINT N'Done. Connection string: Server=' + @@SERVERNAME + N';Database=' + @Database + N';User Id=' + @Name + N';Password=...;TrustServerCertificate=true';
GO
