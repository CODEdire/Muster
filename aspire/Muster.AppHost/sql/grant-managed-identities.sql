/*───────────────────────────────────────────────────────────────────────────────────────────────────────────
  Passwordless SQL — grant the Muster Entra managed identities access to the application database.

  WHAT  Creates a contained database user for each app's user-assigned managed identity (mapped from Entra via
        FROM EXTERNAL PROVIDER), grants CONNECT, and adds it to db_owner.
  WHY   Production binds an EXISTING Azure SQL Server (PersistenceOptions:UseExisting=true), so Aspire does NOT
        emit the auto-grant deployment script it would for a freshly-provisioned server — this is the manual
        one-shot that replaces it. See docs/deployment.md "Passwordless SQL (Entra)".

  RUN   ONCE per environment. Connect to the APPLICATION database (e.g. MusterBot) — NOT master — as an Entra
        admin of the SQL Server (Portal → SQL Database → Query editor, or sqlcmd -G). Idempotent: safe to re-run.

  FILL  Replace the three names below with the actual user-assigned managed identity names. Get them with:
            az identity list -g <env-rg> --query "[].name" -o tsv
        They look like  web_identity-<token>  /  bot_identity-<token>  /  migrations_identity-<token>
        (the <token> is a deterministic per-resource-group suffix, stable across redeploys).
───────────────────────────────────────────────────────────────────────────────────────────────────────────*/

SET NOCOUNT ON;

DECLARE @identities TABLE (name sysname);
INSERT INTO @identities (name) VALUES
    (N'web_identity-REPLACE_ME'),
    (N'bot_identity-REPLACE_ME'),
    (N'migrations_identity-REPLACE_ME');

-- Guard: refuse to run against master (contained users belong in the application DB).
IF DB_NAME() = N'master'
BEGIN
    RAISERROR(N'Run this against the application database (e.g. MusterBot), not master.', 16, 1);
    RETURN;
END

DECLARE @name sysname, @sql nvarchar(max);
DECLARE identity_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @identities;
OPEN identity_cursor;
FETCH NEXT FROM identity_cursor INTO @name;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF @name LIKE N'%REPLACE_ME%'
    BEGIN
        RAISERROR(N'Identity name still contains REPLACE_ME — fill in the real managed identity names first.', 16, 1);
        RETURN;
    END

    -- 1) (Re)map the Entra managed identity to a contained DB user. DROP + CREATE rather than skip-if-exists:
    --    if the managed identity was deleted & recreated (e.g. an identity redeploy), an existing user's SID is
    --    stale and the MI's new token won't match it -> "Login failed for user". Recreating refreshes the SID.
    IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
    BEGIN
        SET @sql = N'DROP USER ' + QUOTENAME(@name) + N';';
        EXEC sp_executesql @sql;
        PRINT N'Dropped existing user ' + @name + N' (refreshing SID)';
    END
    SET @sql = N'CREATE USER ' + QUOTENAME(@name) + N' FROM EXTERNAL PROVIDER;';
    EXEC sp_executesql @sql;
    PRINT N'Created user ' + @name;

    -- 2) CONNECT (explicit; db_owner already implies it, but make the intent unambiguous).
    SET @sql = N'GRANT CONNECT TO ' + QUOTENAME(@name) + N';';
    EXEC sp_executesql @sql;

    -- 3) db_owner — full DDL + DML. The migration job needs DDL; web/bot run all reads/writes under it too.
    IF IS_ROLEMEMBER(N'db_owner', @name) = 0
    BEGIN
        SET @sql = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@name) + N';';
        EXEC sp_executesql @sql;
        PRINT N'Added ' + @name + N' to db_owner';
    END
    ELSE
        PRINT N'User ' + @name + N' already in db_owner';

    FETCH NEXT FROM identity_cursor INTO @name;
END

CLOSE identity_cursor;
DEALLOCATE identity_cursor;
PRINT N'Done.';

-- Verify: every external (Entra) user in THIS database and whether it's in db_owner. Each managed identity
-- you listed above should appear with is_db_owner = 1. DB_NAME() must be the application DB (e.g. MusterBot).
SELECT
    DB_NAME()                                   AS database_name,
    dp.name                                     AS principal_name,
    dp.type_desc,
    dp.authentication_type_desc,
    IS_ROLEMEMBER(N'db_owner', dp.name)         AS is_db_owner
FROM sys.database_principals AS dp
WHERE dp.type IN ('E', 'X')   -- E = EXTERNAL_USER, X = EXTERNAL_GROUP
ORDER BY dp.name;
