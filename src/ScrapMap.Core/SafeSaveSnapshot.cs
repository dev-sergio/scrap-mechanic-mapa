using Microsoft.Data.Sqlite;

namespace ScrapMap.Core;

public sealed class SafeSaveSnapshot : IDisposable
{
    private readonly string _directory;

    private SafeSaveSnapshot(string directory, string databasePath)
    {
        _directory = directory;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public static async Task<SafeSaveSnapshot> CreateAsync(
        string sourceDatabasePath,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(sourceDatabasePath);
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "ScrapMap", "snapshots");
        Directory.CreateDirectory(snapshotRoot);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(snapshotRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var targetPath = Path.Combine(directory, Path.GetFileName(sourcePath));

            try
            {
                var before = FileBundleStamp.Read(sourcePath);
                if (!before.DatabaseExists)
                {
                    throw new IOException("O save não foi encontrado.");
                }

                await Task.Delay(200, cancellationToken);
                if (before != FileBundleStamp.Read(sourcePath))
                {
                    throw new IOException("O save mudou antes da cópia.");
                }

                File.Copy(sourcePath, targetPath, overwrite: true);
                CopyIfPresent(sourcePath + "-wal", targetPath + "-wal");
                CopyIfPresent(sourcePath + "-shm", targetPath + "-shm");
                CopyIfPresent(sourcePath + "-journal", targetPath + "-journal");

                var after = FileBundleStamp.Read(sourcePath);
                if (before != after)
                {
                    throw new IOException("O save mudou durante a cópia.");
                }

                await ValidateAndRecoverCopyAsync(targetPath, cancellationToken);
                return new SafeSaveSnapshot(directory, targetPath);
            }
            catch (Exception exception) when (exception is IOException or SqliteException)
            {
                lastError = exception;
                TryDeleteDirectory(directory);
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new IOException("Não foi possível obter uma cópia estável do save. Tente novamente em alguns segundos.", lastError);
    }

    public void Dispose() => TryDeleteDirectory(_directory);

    private static void CopyIfPresent(string source, string target)
    {
        if (File.Exists(source)) File.Copy(source, target, overwrite: true);
    }

    private static async Task ValidateAndRecoverCopyAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
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
            // Temp cleanup can be retried by the operating system later.
        }
    }

    private sealed record FileBundleStamp(
        bool DatabaseExists,
        long DatabaseWriteTicks,
        long DatabaseLength,
        bool WalExists,
        long WalWriteTicks,
        long WalLength,
        bool ShmExists,
        long ShmWriteTicks,
        long ShmLength,
        bool JournalExists,
        long JournalWriteTicks,
        long JournalLength)
    {
        public static FileBundleStamp Read(string databasePath)
        {
            var database = ReadPart(databasePath);
            var wal = ReadPart(databasePath + "-wal");
            var shm = ReadPart(databasePath + "-shm");
            var journal = ReadPart(databasePath + "-journal");
            return new FileBundleStamp(
                database.Exists, database.Ticks, database.Length,
                wal.Exists, wal.Ticks, wal.Length,
                shm.Exists, shm.Ticks, shm.Length,
                journal.Exists, journal.Ticks, journal.Length);
        }

        private static (bool Exists, long Ticks, long Length) ReadPart(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? (true, file.LastWriteTimeUtc.Ticks, file.Length) : (false, 0, 0);
        }
    }
}
