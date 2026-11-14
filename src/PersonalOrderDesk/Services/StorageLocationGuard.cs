using System.Text.Json;
using System.IO;

namespace PersonalOrderDesk.Storage;

/// <summary>Distinguishes an intentional first run from a missing previously initialized database.</summary>
public static class StorageLocationGuard
{
    public static string ResolveDataDirectory(string? configuredDirectory, string installationDirectory, string defaultDirectory)
    {
        if (configuredDirectory is not null) return Path.GetFullPath(configuredDirectory);
        var registrationPath = Path.Combine(installationDirectory, "storage-location.json");
        return File.Exists(registrationPath) ? Path.GetFullPath(ReadRegistration(registrationPath).DataDirectory)
            : Path.GetFullPath(defaultDirectory);
    }

    public static string RegistrationPath(string installationDirectory, string dataDirectory, bool explicitlyConfigured)
        => Path.Combine(explicitlyConfigured ? dataDirectory : installationDirectory, "storage-location.json");

    public static void Validate(string dataDirectory, string registrationPath)
    {
        var databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "app.db");
        if (!File.Exists(registrationPath))
        {
            // Backup files predate this guard; launcher logs alone may belong to this first startup.
            if (!File.Exists(databasePath) && Directory.Exists(Path.Combine(dataDirectory, "Backups")))
                throw MissingDatabase(databasePath);
            return;
        }

        var registration = ReadRegistration(registrationPath);
        if (!string.Equals(Path.GetFullPath(registration.DataDirectory), Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"数据目录与上次使用不一致，已停止启动。上次：{registration.DataDirectory}；本次：{dataDirectory}。请核对启动配置，未创建空库。");
        if (!File.Exists(databasePath)) throw MissingDatabase(databasePath);
    }

    private static Registration ReadRegistration(string registrationPath)
    {
        try
        {
            var registration = JsonSerializer.Deserialize<Registration>(File.ReadAllText(registrationPath))
                ?? throw new InvalidDataException("数据位置登记为空。");
            if (registration.ProductId != "PersonalOrderDesk" || !registration.Initialized
                || string.IsNullOrWhiteSpace(registration.DataDirectory))
                throw new InvalidDataException("数据位置登记内容无效。");
            return registration;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidOperationException($"数据位置登记损坏，已停止启动，未新建数据库。请保留文件并检查：{registrationPath}", ex);
        }

    }

    public static void RememberInitialized(string dataDirectory, string registrationPath)
    {
        if (!File.Exists(Path.Combine(dataDirectory, "app.db"))) throw MissingDatabase(Path.Combine(dataDirectory, "app.db"));
        Validate(dataDirectory, registrationPath);
        if (File.Exists(registrationPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(registrationPath))!);
        var temporaryPath = registrationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Registration("PersonalOrderDesk", Path.GetFullPath(dataDirectory), true)));
            File.Move(temporaryPath, registrationPath, overwrite: false);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static InvalidOperationException MissingDatabase(string path) => new(
        $"找不到此前使用的数据库：{path}。已停止启动，未创建空库。请检查磁盘和数据目录，或从成对的.db与manifest备份恢复；不要直接重新导入订单。");

    private sealed record Registration(string ProductId, string DataDirectory, bool Initialized);
}
