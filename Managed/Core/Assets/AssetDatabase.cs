using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ArisenEditor.Core.Assets;

/// <summary>
/// A lightweight SQLite database used to index source assets and their Guids.
/// This prevents the editor from needing to parse all .meta files on startup.
/// </summary>
public class AssetDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    public static AssetDatabase Instance { get; private set; } = null!;

    public static void Initialize(string dbPath)
    {
        Instance = new AssetDatabase(dbPath);
    }

    private AssetDatabase(string dbPath)
    {
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Assets (
                Guid TEXT PRIMARY KEY,
                Path TEXT NOT NULL UNIQUE,
                Type TEXT,
                Importer TEXT,
                PackageId TEXT,
                LastModified INTEGER NOT NULL
            );
            
            -- Index for quick path lookups
            CREATE INDEX IF NOT EXISTS idx_assets_path ON Assets(Path);
        ";
        command.ExecuteNonQuery();
        EnsureColumn("Importer", "TEXT");
        EnsureColumn("PackageId", "TEXT");
    }

    public void RegisterAsset(Guid guid, string path, string type, long lastModifiedTimeUtc)
    {
        RegisterAsset(guid, path, type, string.Empty, string.Empty, lastModifiedTimeUtc);
    }

    public void RegisterAsset(
        Guid guid,
        string path,
        string type,
        string importer,
        string packageId,
        long lastModifiedTimeUtc)
    {
        if (guid == Guid.Empty)
        {
            throw new ArgumentException("Asset GUID cannot be empty.", nameof(guid));
        }

        var existingPath = GetPath(guid);
        if (!string.IsNullOrWhiteSpace(existingPath) &&
            !string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Duplicate asset GUID '{guid}' found at '{path}' and '{existingPath}'.");
        }

        if (TryGetGuid(path, out var existingGuid) && existingGuid != guid)
        {
            RemoveAsset(existingGuid);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Assets (Guid, Path, Type, Importer, PackageId, LastModified)
            VALUES ($guid, $path, $type, $importer, $packageId, $lastModified)
            ON CONFLICT(Guid) DO UPDATE SET
                Path=excluded.Path,
                Type=excluded.Type,
                Importer=excluded.Importer,
                PackageId=excluded.PackageId,
                LastModified=excluded.LastModified;
        ";
        command.Parameters.AddWithValue("$guid", guid.ToString());
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$importer", importer);
        command.Parameters.AddWithValue("$packageId", packageId);
        command.Parameters.AddWithValue("$lastModified", lastModifiedTimeUtc);
        
        command.ExecuteNonQuery();
    }

    public bool TryGetGuid(string path, out Guid guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Guid FROM Assets WHERE Path = $path LIMIT 1";
        command.Parameters.AddWithValue("$path", path);

        var result = command.ExecuteScalar();
        if (result != null && Guid.TryParse(result.ToString(), out var g))
        {
            guid = g;
            return true;
        }

        guid = Guid.Empty;
        return false;
    }

    public string? GetPath(Guid guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Path FROM Assets WHERE Guid = $guid LIMIT 1";
        command.Parameters.AddWithValue("$guid", guid.ToString());

        var result = command.ExecuteScalar();
        return result?.ToString();
    }
    
    public void RemoveAsset(Guid guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM Assets WHERE Guid = $guid";
        command.Parameters.AddWithValue("$guid", guid.ToString());
        command.ExecuteNonQuery();
    }
    
    public void RemoveAssetByPath(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM Assets WHERE Path = $path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    public IEnumerable<(Guid Guid, string Path, string Type)> GetAllAssets()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Guid, Path, Type FROM Assets";
        
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out var g))
            {
                 yield return (g, reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
            }
        }
    }

    public IEnumerable<(Guid Guid, string Path, string Type, string Importer, string PackageId)> GetAllAssetRecords()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Guid, Path, Type, Importer, PackageId FROM Assets";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out var g))
            {
                yield return (
                    g,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
            }
        }
    }

    public int PruneMissingAssets(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        }

        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var missingGuids = new List<Guid>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT Guid, Path FROM Assets";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var guid))
                {
                    continue;
                }

                var registeredPath = reader.GetString(1);
                var sourcePath = Path.IsPathFullyQualified(registeredPath)
                    ? registeredPath
                    : Path.Combine(
                        fullWorkspaceRoot,
                        registeredPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(Path.GetFullPath(sourcePath)))
                {
                    missingGuids.Add(guid);
                }
            }
        }

        if (missingGuids.Count == 0)
        {
            return 0;
        }

        using var transaction = _connection.BeginTransaction();
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM Assets WHERE Guid = $guid";
        var guidParameter = delete.Parameters.Add("$guid", SqliteType.Text);
        foreach (var guid in missingGuids)
        {
            guidParameter.Value = guid.ToString();
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return missingGuids.Count;
    }

    private void EnsureColumn(string name, string type)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Assets)";

        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Assets ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
