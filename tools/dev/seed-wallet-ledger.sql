/*
  Dev-only: seed ~5 months of wallet ledger entries for one spendable currency so the
  wallet sparkline and the analytics candle chart have data to render. Safe to re-run
  (it only INSERTs new ledger rows). After running, open the guild's Currencies admin
  page and click "Rebuild" on the currency to resync the wallet-balance cache (leaderboard
  / rank). Balances on the wallet + analytics pages read from the ledger directly, so they
  update immediately without the rebuild.

  HOW TO FIND THE IDS:
    @GuildId / @UserId : the numbers in the wallet URL /guilds/<GuildId>/... and your member.
                         (UserId = your Discord user id; see the Users/GuildMembers table.)
    @Code              : the currency code shown next to the balance, e.g. EXT / COIN.

  This targets SQL Server (the app's provider). ulong columns are decimal(20,0).
*/

DECLARE @GuildId DECIMAL(20,0) = /* <-- your guild id */ 0;
DECLARE @UserId  DECIMAL(20,0) = /* <-- your user id  */ 0;
DECLARE @Code    NVARCHAR(50)  = N'EXT';
DECLARE @Count   INT           = 50;   -- entries spread ~3 days apart (~150 days back)

DECLARE @CurrencyId UNIQUEIDENTIFIER =
    (SELECT TOP 1 Id FROM Currencies WHERE GuildId = @GuildId AND Code = @Code AND IsSpendable = 1);

IF @CurrencyId IS NULL
BEGIN
    RAISERROR('No spendable currency with that code in that guild. Check @GuildId / @Code.', 16, 1);
    RETURN;
END;

;WITH n AS (
    SELECT 0 AS i
    UNION ALL SELECT i + 1 FROM n WHERE i < @Count - 1
)
INSERT INTO CurrencyLedgerEntries (GuildId, UserId, CurrencyId, SeasonId, Amount, SourceType, SourceId, OccurredAt, Reason)
SELECT
    @GuildId,
    @UserId,
    @CurrencyId,
    NULL,
    -- mean-positive, occasionally negative => rising balance with dips (red + green candles)
    CAST((ABS(CHECKSUM(NEWID())) % 24) - 6 AS BIGINT),
    -- mix of sources: 0 Session, 1 Quest, 2 Muster, 10 Shop, 6 Transfer
    (CASE ABS(CHECKSUM(NEWID())) % 5 WHEN 3 THEN 10 WHEN 4 THEN 6 ELSE ABS(CHECKSUM(NEWID())) % 3 END),
    NULL,
    DATEADD(MINUTE, -(ABS(CHECKSUM(NEWID())) % 600), DATEADD(DAY, -(i * 3), SYSDATETIMEOFFSET())),
    CONCAT(N'Seed entry #', i)
FROM n
OPTION (MAXRECURSION 200);

PRINT CONCAT('Seeded ', @Count, ' ledger entries for currency ', @Code, '. Now click Rebuild on the currency admin page.');
