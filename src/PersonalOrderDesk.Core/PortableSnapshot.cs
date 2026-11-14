using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PersonalOrderDesk.Client;

public static class PortableSnapshot
{
    public const long MaxBytes=100*1024*1024;
    public static async Task<byte[]> Create(string databasePath,ServerInfo info,string[] migrations,string scratch,CancellationToken ct=default)
    {
        Directory.CreateDirectory(scratch); var temporary=Path.Combine(scratch,Guid.NewGuid().ToString("N")+".db");
        try {
            await using(var source=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=databasePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            await using(var target=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=temporary,Pooling=false}.ToString()))
            { await source.OpenAsync(ct);await target.OpenAsync(ct);source.BackupDatabase(target); }
            var bytes=await File.ReadAllBytesAsync(temporary,ct); if(bytes.LongLength>MaxBytes)throw new InvalidOperationException("数据库超过副本大小限制。");
            var manifest=new SnapshotManifest("PersonalOrderDesk",1,info.InstanceId,info.Name,DateTimeOffset.UtcNow,migrations,Convert.ToHexString(SHA256.HashData(bytes)));
            using var buffer=new MemoryStream();using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true)) {
                await using(var stream=zip.CreateEntry("app.db",CompressionLevel.Fastest).Open()) await stream.WriteAsync(bytes,ct);
                await using(var stream=zip.CreateEntry("manifest.json").Open()) await JsonSerializer.SerializeAsync(stream,manifest,DeskJson.Options,ct);
            } return buffer.ToArray();
        } finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<SnapshotManifest> ValidateAndWrite(Stream archive,string target,string expectedInstance,IReadOnlyCollection<string> knownMigrations,CancellationToken ct=default)
    {
        using var zip=new ZipArchive(archive,ZipArchiveMode.Read,true);
        if(zip.Entries.Count!=2)throw new InvalidDataException("副本文件内容不正确。");
        var database=zip.GetEntry("app.db")??throw new InvalidDataException("副本缺少数据库。");
        var metadata=zip.GetEntry("manifest.json")??throw new InvalidDataException("副本缺少清单。");
        if(database.Length>MaxBytes||metadata.Length>65536)throw new InvalidDataException("副本超过大小限制。");
        SnapshotManifest manifest; await using(var stream=metadata.Open()) manifest=await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream,DeskJson.Options,ct)??throw new InvalidDataException("副本清单无效。");
        if(manifest.ProductId!="PersonalOrderDesk"||manifest.FormatVersion!=1||manifest.InstanceId!=expectedInstance)throw new InvalidDataException("副本来源与已连接电脑不一致。");
        if(manifest.Migrations is null||!manifest.Migrations.OrderBy(x=>x).SequenceEqual(knownMigrations.OrderBy(x=>x)))throw new InvalidDataException("电脑数据版本较新，请先更新客户端。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            await using(var input=database.Open())await using(var output=File.Create(temporary)) {
                var buffer=new byte[81920];long total=0;int n;
                while((n=await input.ReadAsync(buffer,ct))>0){ total+=n;if(total>MaxBytes)throw new InvalidDataException("副本超过大小限制。");await output.WriteAsync(buffer.AsMemory(0,n),ct); }
            }
            await using(var stream=File.OpenRead(temporary)) if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(manifest.DatabaseSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("副本校验失败，原副本未替换。");
            await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=temporary,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())) {
                await db.OpenAsync(ct);await using var command=db.CreateCommand();command.CommandText="PRAGMA integrity_check";
                if(Convert.ToString(await command.ExecuteScalarAsync(ct))!="ok")throw new InvalidDataException("副本完整性检查失败。");
                command.CommandText="PRAGMA foreign_key_check";await using(var reader=await command.ExecuteReaderAsync(ct))if(await reader.ReadAsync(ct))throw new InvalidDataException("副本关联检查失败。");
                command.CommandText="SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";var applied=new List<string>();await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))applied.Add(reader.GetString(0));
                if(!applied.SequenceEqual(manifest.Migrations.OrderBy(x=>x)))throw new InvalidDataException("副本数据版本与清单不符。");
            }
            if(File.Exists(target))File.Copy(target,target+".previous",true);
            File.Move(temporary,target,true);return manifest;
        } finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
