using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace POS.Shared
{
    // ---------------------------------------------------------------------------
    // Domain records — returned by read methods instead of raw tuples
    // ---------------------------------------------------------------------------

    public record CashierRecord(int Id, string Name);

    public record TransactionRecord(
        int Id,
        int ShiftId,
        decimal Amount,
        string CardMethod,
        string Result,
        DateTime TimestampUtc);

    public record ShiftSummary(
        int ShiftId,
        int CashierId,
        string CashierName,
        DateTime StartedAtUtc,
        DateTime? EndedAtUtc,
        string Status,
        int TransactionCount,
        decimal TotalApproved);

    // ---------------------------------------------------------------------------
    // Status constants — no more magic strings scattered across SQL
    // ---------------------------------------------------------------------------

    public static class DbStatus
    {
        public const string Open = "OPEN";
        public const string Closed = "CLOSED";
        public const string Active = "ACTIVE";
    }

    // ---------------------------------------------------------------------------
    // Interface — makes the class mockable and unit-testable
    // ---------------------------------------------------------------------------

    public interface IPosDatabase
    {
        void Initialize();
        Task<int> CreateCashierAsync(string name, string pin);
        Task<CashierRecord?> LoginCashierAsync(string pin);
        Task<int> OpenBusinessDayAsync();
        Task CloseBusinessDayAsync(int businessDayId);
        Task<int> StartShiftAsync(int cashierId, int businessDayId);
        Task EndShiftAsync(int shiftId);
        Task<int> RecordTransactionAsync(int shiftId, decimal amount, string cardMethod, string result);
        Task<IReadOnlyList<TransactionRecord>> GetTransactionsByShiftAsync(int shiftId);
        Task<ShiftSummary?> GetShiftSummaryAsync(int shiftId);
        Task<IReadOnlyList<ShiftSummary>> GetShiftsByBusinessDayAsync(int businessDayId);
    }

    // ---------------------------------------------------------------------------
    // Implementation
    // ---------------------------------------------------------------------------

    public sealed class PosDatabase : IPosDatabase
    {
        private readonly string _connectionString;

        // Inject the DB path so tests can point to :memory: or a temp file.
        public PosDatabase(string? dbPath = null)
        {
            string path = dbPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "POS-SIM", "posdata.db");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _connectionString = $"Data Source={path}";
        }

        // ---------------------------------------------------------------------------
        // Connection helper — one place to open a connection; no copy-paste
        // ---------------------------------------------------------------------------

        private async Task<SqliteConnection> OpenConnectionAsync()
        {
            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            // Enforce foreign keys — SQLite disables them by default.
            using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
            await fkCmd.ExecuteNonQueryAsync();

            return conn;
        }

        // ---------------------------------------------------------------------------
        // PIN hashing — PBKDF2-SHA256, 100k iterations, timing-safe compare
        // ---------------------------------------------------------------------------

        private static string HashPin(string pin)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            using var kdf = new Rfc2898DeriveBytes(pin, salt, 100_000, HashAlgorithmName.SHA256);
            byte[] hash = kdf.GetBytes(32);
            return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
        }

        private static bool VerifyPin(string pin, string stored)
        {
            // Backward compatibility: legacy rows may still store plaintext PIN.
            if (!stored.Contains(':'))
            {
                return string.Equals(pin, stored, StringComparison.Ordinal);
            }

            var parts = stored.Split(':', 2);
            if (parts.Length != 2) return false;

            byte[] salt;
            byte[] expectedHash;
            try
            {
                salt = Convert.FromBase64String(parts[0]);
                expectedHash = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException)
            {
                return false;
            }

            using var kdf = new Rfc2898DeriveBytes(pin, salt, 100_000, HashAlgorithmName.SHA256);
            byte[] actualHash = kdf.GetBytes(32);

            // Constant-time compare to prevent timing attacks.
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }

        private static bool IsHashedPin(string value)
        {
            return value.Contains(':');
        }

        // ---------------------------------------------------------------------------
        // Schema initialisation — DDL wrapped in a transaction
        // ---------------------------------------------------------------------------

        public void Initialize()
        {
            PosLogger.Info("DATABASE", "Initializing database...");

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
            fkCmd.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            try
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Cashiers (
                        Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name      TEXT    NOT NULL,
                        Pin   TEXT    NOT NULL,
                        CreatedAt TEXT    NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS BusinessDays (
                        Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                        OpenedAt TEXT    NOT NULL,
                        ClosedAt TEXT,
                        Status   TEXT    NOT NULL CHECK(Status IN ('OPEN','CLOSED'))
                    );

                    CREATE TABLE IF NOT EXISTS Shifts (
                        Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        CashierId     INTEGER NOT NULL,
                        BusinessDayId INTEGER NOT NULL,
                        StartedAt     TEXT    NOT NULL,
                        EndedAt       TEXT,
                        Status        TEXT    NOT NULL CHECK(Status IN ('ACTIVE','CLOSED')),
                        FOREIGN KEY(CashierId)     REFERENCES Cashiers(Id),
                        FOREIGN KEY(BusinessDayId) REFERENCES BusinessDays(Id)
                    );

                    CREATE TABLE IF NOT EXISTS Transactions (
                        Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        ShiftId    INTEGER NOT NULL,
                        Amount     REAL    NOT NULL,
                        CardMethod TEXT    NOT NULL,
                        Result     TEXT    NOT NULL,
                        Timestamp  TEXT    NOT NULL,
                        FOREIGN KEY(ShiftId) REFERENCES Shifts(Id)
                    );
                ";
                cmd.ExecuteNonQuery();

                SeedDefaultCashierIfNeeded(conn, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            PosLogger.Info("DATABASE", $"Database ready. Connection: {_connectionString}");
        }

        // ---------------------------------------------------------------------------
        // Cashiers
        // ---------------------------------------------------------------------------

        public async Task<int> CreateCashierAsync(string name, string pin)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cashier name cannot be empty.", nameof(name));
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN cannot be empty.", nameof(pin));

            string Pin = HashPin(pin);

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Cashiers (Name, Pin, CreatedAt)
                VALUES ($name, $Pin, $created);
            ";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$Pin", Pin);
            cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();

            int id = await GetLastInsertRowIdAsync(conn);
            PosLogger.Info("DATABASE", $"Cashier created: {name} (ID: {id})");
            return id;
        }

        /// <summary>
        /// Logs in a cashier by PIN. Iterates all cashiers with a timing-safe hash
        /// compare — acceptable for a simulator with a small cashier table.
        /// </summary>
        public async Task<CashierRecord?> LoginCashierAsync(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                PosLogger.Warn("DATABASE", "Login attempted with empty PIN.");
                return null;
            }

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Pin FROM Cashiers;";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                int cashierId = reader.GetInt32(0);
                string cashierName = reader.GetString(1);
                string storedHash = reader.GetString(2);
                if (VerifyPin(pin, storedHash))
                {
                    // If this cashier still uses legacy plaintext PIN storage,
                    // transparently migrate to hashed storage on successful login.
                    if (!IsHashedPin(storedHash))
                    {
                        await reader.CloseAsync();
                        var upgradeCmd = conn.CreateCommand();
                        upgradeCmd.CommandText = @"
                            UPDATE Cashiers
                            SET Pin = $newPin
                            WHERE Id = $id;
                        ";
                        upgradeCmd.Parameters.AddWithValue("$newPin", HashPin(pin));
                        upgradeCmd.Parameters.AddWithValue("$id", cashierId);
                        await upgradeCmd.ExecuteNonQueryAsync();
                    }

                    var record = new CashierRecord(cashierId, cashierName);
                    PosLogger.Info("DATABASE", $"Cashier login: {record.Name} (ID: {record.Id})");
                    return record;
                }
            }

            PosLogger.Warn("DATABASE", "Failed cashier login attempt.");
            return null;
        }

        // ---------------------------------------------------------------------------
        // Business days
        // ---------------------------------------------------------------------------

        public async Task<int> OpenBusinessDayAsync()
        {
            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO BusinessDays (OpenedAt, Status)
                VALUES ($opened, $status);
            ";
            cmd.Parameters.AddWithValue("$opened", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$status", DbStatus.Open);
            await cmd.ExecuteNonQueryAsync();

            int id = await GetLastInsertRowIdAsync(conn);
            PosLogger.Info("DATABASE", $"Business day opened (ID: {id})");
            return id;
        }

        public async Task CloseBusinessDayAsync(int businessDayId)
        {
            if (businessDayId <= 0)
                throw new ArgumentOutOfRangeException(nameof(businessDayId));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE BusinessDays
                SET    ClosedAt = $closed, Status = $status
                WHERE  Id = $id AND Status = $open;
            ";
            cmd.Parameters.AddWithValue("$closed", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$status", DbStatus.Closed);
            cmd.Parameters.AddWithValue("$open", DbStatus.Open);
            cmd.Parameters.AddWithValue("$id", businessDayId);

            int rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                throw new InvalidOperationException(
                    $"Business day {businessDayId} is not open or does not exist.");

            PosLogger.Info("DATABASE", $"Business day closed (ID: {businessDayId})");
        }

        // ---------------------------------------------------------------------------
        // Shifts
        // ---------------------------------------------------------------------------

        public async Task<int> StartShiftAsync(int cashierId, int businessDayId)
        {
            if (cashierId <= 0) throw new ArgumentOutOfRangeException(nameof(cashierId));
            if (businessDayId <= 0) throw new ArgumentOutOfRangeException(nameof(businessDayId));

            await using var conn = await OpenConnectionAsync();

            // Verify the business day exists and is open before inserting.
            var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT Status FROM BusinessDays WHERE Id = $id;";
            checkCmd.Parameters.AddWithValue("$id", businessDayId);
            var statusObj = await checkCmd.ExecuteScalarAsync();

            if (statusObj is null)
                throw new InvalidOperationException(
                    $"Business day {businessDayId} does not exist.");
            if ((string)statusObj != DbStatus.Open)
                throw new InvalidOperationException(
                    $"Business day {businessDayId} is not open (Status: {statusObj}).");

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Shifts (CashierId, BusinessDayId, StartedAt, Status)
                VALUES ($cid, $bid, $started, $status);
            ";
            cmd.Parameters.AddWithValue("$cid", cashierId);
            cmd.Parameters.AddWithValue("$bid", businessDayId);
            cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$status", DbStatus.Active);
            await cmd.ExecuteNonQueryAsync();

            int id = await GetLastInsertRowIdAsync(conn);
            PosLogger.Info("DATABASE", $"Shift started (ID: {id}, CashierID: {cashierId})");
            return id;
        }

        public async Task EndShiftAsync(int shiftId)
        {
            if (shiftId <= 0) throw new ArgumentOutOfRangeException(nameof(shiftId));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE Shifts
                SET    EndedAt = $ended, Status = $closed
                WHERE  Id = $id AND Status = $active;
            ";
            cmd.Parameters.AddWithValue("$ended", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$closed", DbStatus.Closed);
            cmd.Parameters.AddWithValue("$active", DbStatus.Active);
            cmd.Parameters.AddWithValue("$id", shiftId);

            int rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                throw new InvalidOperationException(
                    $"Shift {shiftId} is not active or does not exist.");

            PosLogger.Info("DATABASE", $"Shift ended (ID: {shiftId})");
        }

        // ---------------------------------------------------------------------------
        // Transactions
        // ---------------------------------------------------------------------------

        public async Task<int> RecordTransactionAsync(
            int shiftId, decimal amount, string cardMethod, string result)
        {
            if (shiftId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shiftId));
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Amount cannot be negative.");
            if (string.IsNullOrWhiteSpace(cardMethod))
                throw new ArgumentException("Card method cannot be empty.", nameof(cardMethod));
            if (string.IsNullOrWhiteSpace(result))
                throw new ArgumentException("Result cannot be empty.", nameof(result));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Transactions (ShiftId, Amount, CardMethod, Result, Timestamp)
                VALUES ($sid, $amount, $method, $result, $ts);
            ";
            cmd.Parameters.AddWithValue("$sid", shiftId);
            cmd.Parameters.AddWithValue("$amount", (double)amount);
            cmd.Parameters.AddWithValue("$method", cardMethod);
            cmd.Parameters.AddWithValue("$result", result);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();

            int id = await GetLastInsertRowIdAsync(conn);
            PosLogger.Info("DATABASE",
                $"Transaction recorded: {result} {amount:C} via {cardMethod} (ID: {id}, ShiftID: {shiftId})");
            return id;
        }

        public async Task<IReadOnlyList<TransactionRecord>> GetTransactionsByShiftAsync(int shiftId)
        {
            if (shiftId <= 0) throw new ArgumentOutOfRangeException(nameof(shiftId));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, ShiftId, Amount, CardMethod, Result, Timestamp
                FROM   Transactions
                WHERE  ShiftId = $sid
                ORDER  BY Timestamp ASC;
            ";
            cmd.Parameters.AddWithValue("$sid", shiftId);

            var records = new List<TransactionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new TransactionRecord(
                    Id: reader.GetInt32(0),
                    ShiftId: reader.GetInt32(1),
                    Amount: (decimal)reader.GetDouble(2),
                    CardMethod: reader.GetString(3),
                    Result: reader.GetString(4),
                    TimestampUtc: DateTime.Parse(reader.GetString(5)).ToUniversalTime()));
            }

            return records;
        }

        public async Task<ShiftSummary?> GetShiftSummaryAsync(int shiftId)
        {
            if (shiftId <= 0) throw new ArgumentOutOfRangeException(nameof(shiftId));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    s.Id,
                    s.CashierId,
                    c.Name,
                    s.StartedAt,
                    s.EndedAt,
                    s.Status,
                    COUNT(t.Id)                                           AS TxCount,
                    COALESCE(SUM(CASE WHEN t.Result = 'APPROVED'
                                     THEN t.Amount ELSE 0 END), 0)       AS TotalApproved
                FROM   Shifts s
                JOIN   Cashiers c ON c.Id = s.CashierId
                LEFT   JOIN Transactions t ON t.ShiftId = s.Id
                WHERE  s.Id = $sid
                GROUP  BY s.Id;
            ";
            cmd.Parameters.AddWithValue("$sid", shiftId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return BuildShiftSummary(reader);
        }

        public async Task<IReadOnlyList<ShiftSummary>> GetShiftsByBusinessDayAsync(int businessDayId)
        {
            if (businessDayId <= 0) throw new ArgumentOutOfRangeException(nameof(businessDayId));

            await using var conn = await OpenConnectionAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    s.Id,
                    s.CashierId,
                    c.Name,
                    s.StartedAt,
                    s.EndedAt,
                    s.Status,
                    COUNT(t.Id)                                           AS TxCount,
                    COALESCE(SUM(CASE WHEN t.Result = 'APPROVED'
                                     THEN t.Amount ELSE 0 END), 0)       AS TotalApproved
                FROM   Shifts s
                JOIN   Cashiers c ON c.Id = s.CashierId
                LEFT   JOIN Transactions t ON t.ShiftId = s.Id
                WHERE  s.BusinessDayId = $bid
                GROUP  BY s.Id
                ORDER  BY s.StartedAt ASC;
            ";
            cmd.Parameters.AddWithValue("$bid", businessDayId);

            var summaries = new List<ShiftSummary>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                summaries.Add(BuildShiftSummary(reader));

            return summaries;
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static ShiftSummary BuildShiftSummary(SqliteDataReader reader)
        {
            string? endedAtRaw = reader.IsDBNull(4) ? null : reader.GetString(4);

            return new ShiftSummary(
                ShiftId: reader.GetInt32(0),
                CashierId: reader.GetInt32(1),
                CashierName: reader.GetString(2),
                StartedAtUtc: DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                EndedAtUtc: endedAtRaw is null
                                      ? null
                                      : DateTime.Parse(endedAtRaw).ToUniversalTime(),
                Status: reader.GetString(5),
                TransactionCount: reader.GetInt32(6),
                TotalApproved: (decimal)reader.GetDouble(7));
        }

        private static void SeedDefaultCashierIfNeeded(SqliteConnection conn, SqliteTransaction tx)
        {
            var countCmd = conn.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM Cashiers;";
            var count = Convert.ToInt32(countCmd.ExecuteScalar());

            if (count > 0)
            {
                return;
            }

            var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"
                INSERT INTO Cashiers (Name, Pin, CreatedAt)
                VALUES ($name, $Pin, $created);
            ";
            insertCmd.Parameters.AddWithValue("$name", "Default Cashier");
            insertCmd.Parameters.AddWithValue("$Pin", HashPin("0000"));
            insertCmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
            insertCmd.ExecuteNonQuery();

            PosLogger.Info("DATABASE", "Seeded default cashier with PIN 0000.");
        }

        private static async Task<int> GetLastInsertRowIdAsync(SqliteConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_insert_rowid();";
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
    }
}
