using PersonalOrderDesk.Storage;

namespace PersonalOrderDesk.Tests;

public sealed class StorageLocationGuardTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalOrderDesk-storage-tests-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "Data");
    private string Registration => Path.Combine(root, "Program", "storage-location.json");

    [Fact]
    public void FirstRunIsAllowedAndExistingDatabasePersistsAcrossRepeatedStarts()
    {
        StorageLocationGuard.Validate(Data, Registration);
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "app.db"), "test database content");
        StorageLocationGuard.RememberInitialized(Data, Registration);
        StorageLocationGuard.Validate(Data, Registration);
        StorageLocationGuard.RememberInitialized(Data, Registration);
        Assert.Equal("test database content", File.ReadAllText(Path.Combine(Data, "app.db")));
    }

    [Fact]
    public void MissingPreviouslyInitializedDirectoryIsRejectedWithoutCreatingAnEmptyDatabase()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "app.db"), "previous database");
        StorageLocationGuard.RememberInitialized(Data, Registration);
        Directory.Delete(Data, recursive: true);
        Assert.Contains("未创建空库", Assert.Throws<InvalidOperationException>(() => StorageLocationGuard.Validate(Data, Registration)).Message);
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void ChangedDirectoryCannotSilentlyReplaceRegisteredLocation()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "app.db"), "previous database");
        StorageLocationGuard.RememberInitialized(Data, Registration);
        var other = Path.Combine(root, "Other");
        Assert.Contains("不一致", Assert.Throws<InvalidOperationException>(() => StorageLocationGuard.Validate(other, Registration)).Message);
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public void RegisteredLocationIsUsedEvenWhenDefaultChangesAndMissingDataStillBlocksStartup()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "app.db"), "previous database");
        StorageLocationGuard.RememberInitialized(Data, Registration);
        var resolved = StorageLocationGuard.ResolveDataDirectory(null, Path.GetDirectoryName(Registration)!, Path.Combine(root, "NewDefault"));
        Assert.Equal(Path.GetFullPath(Data), resolved);
        Directory.Delete(Data, recursive: true);
        Assert.Throws<InvalidOperationException>(() => StorageLocationGuard.Validate(resolved, Registration));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void CorruptRegistrationIsRejectedInsteadOfReinitializing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Registration)!);
        File.WriteAllText(Registration, "broken JSON");
        Assert.Throws<InvalidOperationException>(() => StorageLocationGuard.Validate(Data, Registration));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void LegacyBackupEvidenceWithoutDatabasePreventsSilentFirstRun()
    {
        Directory.CreateDirectory(Path.Combine(Data, "Backups"));
        Assert.Throws<InvalidOperationException>(() => StorageLocationGuard.Validate(Data, Registration));
        Assert.False(File.Exists(Path.Combine(Data, "app.db")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
