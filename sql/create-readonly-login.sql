/*
    SqlLookout: read-only monitoring login.

    Run once on EACH monitored instance, as a sysadmin. Edit the two lines marked EDIT.
    The login can read metadata and statistics. It cannot read table data, change anything, or run
    arbitrary code: no sysadmin, no db_owner, no write permission anywhere.

    Works on SQL Server 2008 and later (uses sp_addrolemember, not ALTER ROLE ... ADD MEMBER, which is 2012+).
    For a Windows login replace the CREATE LOGIN line with:   CREATE LOGIN [DOMAIN\account] FROM WINDOWS;
*/
USE master;
GO

DECLARE @Name     sysname       = N'sqllookout';                    -- EDIT: login name
DECLARE @Password nvarchar(128) = N'CHANGE-ME-to-a-long-random-password';  -- EDIT: SQL login only

IF SUSER_ID(@Name) IS NULL
BEGIN
    DECLARE @create nvarchar(max) =
        N'CREATE LOGIN ' + QUOTENAME(@Name) + N' WITH PASSWORD = N''' + REPLACE(@Password, N'''', N'''''') + N''', CHECK_POLICY = ON, DEFAULT_DATABASE = master;';
    EXEC (@create);
END

DECLARE @sql nvarchar(max);

-- VIEW SERVER STATE: DMVs (disk volumes, waits) and DBCC SQLPERF(LOGSPACE) for log usage.
SET @sql = N'GRANT VIEW SERVER STATE TO ' + QUOTENAME(@Name) + N';';
EXEC (@sql);

-- VIEW ANY DEFINITION: lets the login see every database's files and state in sys.databases / sys.master_files.
SET @sql = N'GRANT VIEW ANY DEFINITION TO ' + QUOTENAME(@Name) + N';';
EXEC (@sql);

-- CONNECT ANY DATABASE (2014+): lets DATABASEPROPERTYEX('LastGoodCheckDbTime') answer for every database.
-- Older versions do not have this permission; the DBCC check then shows "unknown" for databases the login cannot enter.
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 12
BEGIN
    SET @sql = N'GRANT CONNECT ANY DATABASE TO ' + QUOTENAME(@Name) + N';';
    EXEC (@sql);
END
GO

-- msdb: backup history, suspect pages and (when SQL Server Agent exists) job status. Read-only.
USE msdb;
GO

DECLARE @Name sysname = N'sqllookout';                               -- EDIT: same login name as above
DECLARE @sql  nvarchar(max);

IF USER_ID(@Name) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@Name) + N' FOR LOGIN ' + QUOTENAME(@Name) + N';';
    EXEC (@sql);
END

SET @sql = N'GRANT SELECT ON dbo.backupset TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.suspect_pages TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.sysjobs TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.sysjobservers TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.sysjobhistory TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.sysjobactivity TO ' + QUOTENAME(@Name) + N';'
         + N'GRANT SELECT ON dbo.syssessions TO ' + QUOTENAME(@Name) + N';';
EXEC (@sql);

-- Log shipping monitor tables (only where log shipping was ever set up).
IF OBJECT_ID(N'dbo.log_shipping_monitor_primary') IS NOT NULL
BEGIN
    SET @sql = N'GRANT SELECT ON dbo.log_shipping_monitor_primary TO ' + QUOTENAME(@Name) + N';';
    EXEC (@sql);
END
IF OBJECT_ID(N'dbo.log_shipping_monitor_secondary') IS NOT NULL
BEGIN
    SET @sql = N'GRANT SELECT ON dbo.log_shipping_monitor_secondary TO ' + QUOTENAME(@Name) + N';';
    EXEC (@sql);
END
GO

/*
    What each permission is for:
      VIEW SERVER STATE         disk space per volume, DBCC SQLPERF(LOGSPACE), waits, memory, blocking, availability group health
      VIEW ANY DEFINITION       database list, state, file sizes
      CONNECT ANY DATABASE      last good DBCC CHECKDB time for every database (2014+)
      msdb SELECT (7 tables)    last backup times, suspect pages, Agent job outcome, history and running jobs.
                                Plus the two log shipping monitor tables when they exist.
                                Granted table by table on purpose: SQLAgentReaderRole does not allow reading
                                these tables directly and would add rights that SqlLookout does not use.

    Known limit: on SQL Server before 2016 SP2 the last good CHECKDB time can only be read with DBCC DBINFO,
    which those versions allow only to sysadmin. With this login those databases show "unknown" and raise no alert.
*/
