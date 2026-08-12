using Microsoft.Data.Sqlite;

namespace ScrapMap.Core;

public sealed class SafeSaveSnapshot : IDisposable
{
    private static readonly TimeSpan DefaultStablePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private const int MaximumAttempts = 6;

    private readonly string _directory;

    private SafeSaveSnapshot(string directory, string databasePath)
    {
        _directory = directory;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public static async Task<SafeSaveSnapshot> CreateAsync(
        string sourceDatabasePath,
        TimeSpan? minimumStablePeriod = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(sourceDatabasePath);
        var stablePeriod = minimumStablePeriod ?? DefaultStablePeriod;
        if (stablePeriod < ProbeInterval) stablePeriod = ProbeInterval;

        var snapshotRoot = Path.Combine(Path.GetTempPath(), "ScrapMap", "snapshots");
        Directory.CreateDirectory(snapshotRoot);

        Exception? lastError = null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(snapshotRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var targetPath = Path.Combine(directory, Path.GetFileName(sourcePath));

            try
            {
                var stableStamp = await WaitForStableBundleAsync(
                    sourcePath,
                    stablePeriod,
                    cancellationToken);

                await CopyFileWithoutLockingGameAsync(sourcePath, targetPath, cancellationToken);
                await CopyIfPresentAsync(sourcePath + "-wal", targetPath + "-wal", cancellationToken);
                await CopyIfPresentAsync(sourcePath + "-journal", targetPath + "-journal", cancellationToken);

                if (stableStamp != FileBundleStamp.Read(sourcePath))
                {
                    throw new IOException("O save mudou durante a criação da cópia.");
                }

                // This opens and may checkpoint only the private copy. The source save
                // is never passed to SQLite anywhere in this workflow.
                await ValidateAndRecoverCopyAsync(targetPath, cancellationToken);
                return new SafeSaveSnapshot(directory, targetPath);
            }
            catch (Exception exception) when (exception is IOException or SqliteException)
            {
                lastError = exception;
                TryDeleteDirectory(directory);
                await Task.Delay(ProbeInterval, cancellationToken);
            }
        }

        throw new IOException(
            "Não foi possível obter uma cópia estável do save. O jogo provavelmente ainda está gravando; tente novamente em alguns segundos.",
            lastError);
    }

    public void Dispose() => TryDeleteDirectory(_directory);

    private static async Task<FileBundleStamp> WaitForStableBundleAsync(
        string sourcePath,
        TimeSpan stablePeriod,
        CancellationToken cancellationToken)
    {
        var stamp = FileBundleStamp.Read(sourcePath);
        if (!stamp.DatabaseExists) throw new IOException("O save não foi encontrado.");

        var deadline = DateTime.UtcNow + stablePeriod + TimeSpan.FromSeconds(12);
        var unchangedSince = DateTime.UtcNow;
        while (DateTime.UtcNow - unchangedSince < stablePeriod)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new IOException("O jogo continuou gravando durante a espera pela cópia.");
            }
            await Task.Delay(ProbeInterval, cancellationToken);
            var current = FileBundleStamp.Read(sourcePath);
            if (!current.DatabaseExists) throw new IOException("O save desapareceu durante a cópia.");
            if (current == stamp) continue;
            stamp = current;
            unchangedSince = DateTime.UtcNow;
        }

        return stamp;
    }

    private static async Task CopyIfPresentAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source)) return;
        await CopyFileWithoutLockingGameAsync(source, target, cancellationToken);
    }

    private static async Task CopyFileWithoutLockingGameAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, bufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task ValidateAndRecoverCopyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var integrityCheck = connection.CreateCommand();
        integrityCheck.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await integrityCheck.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"A cópia do save não passou na verificação de integridade: {result}");
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A future application start can clean temporary snapshots left in use.
        }
    }

    private sealed record FileBundleStamp(
        bool DatabaseExists,
        long DatabaseWriteTicks,
        long DatabaseLength,
        bool WalExists,
        long WalWriteTicks,
        long WalLength,
        bool JournalExists,
        long JournalWriteTicks,
        long JournalLength)
    {
        public static FileBundleStamp Read(string databasePath)
        {
            var database = ReadPart(databasePath);
            var wal = ReadPart(databasePath + "-wal");
            var journal = ReadPart(databasePath + "-journal");
            return new FileBundleStamp(
                database.Exists, database.Ticks, database.Length,
                wal.Exists, wal.Ticks, wal.Length,
                journal.Exists, journal.Ticks, journal.Length);
        }

        private static (bool Exists, long Ticks, long Length) ReadPart(string path)
        {
            var file = new FileInfo(path);
            return file.Exists
                ? (true, file.LastWriteTimeUtc.Ticks, file.Length)
                : (false, 0, 0);
        }
    }
}
