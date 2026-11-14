using PersonalOrderDesk.Api;
using PersonalOrderDesk.Client;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Services;
using System.Text.Json;
using PersonalOrderDesk.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
var configuredDataPath = builder.Configuration["PersonalOrderDesk:DataDirectory"]
    ?? Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_DATA_DIR");
var dataPath = StorageLocationGuard.ResolveDataDirectory(configuredDataPath, AppContext.BaseDirectory,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalOrderDesk"));
builder.Configuration["PersonalOrderDesk:DataDirectory"] = dataPath;
var registrationPath = StorageLocationGuard.RegistrationPath(AppContext.BaseDirectory, dataPath,
    configuredDataPath is not null && Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_DEFAULT_STORAGE") != "1");
StorageLocationGuard.Validate(dataPath, registrationPath);
Directory.CreateDirectory(dataPath);
var networkAccess = new NetworkAccessService(dataPath);
builder.Services.AddSingleton(networkAccess);
builder.Services.AddDataProtection().SetApplicationName("PersonalOrderDesk").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "Keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Access/Login";
    options.Cookie.Name = "PersonalOrderDesk.Lan";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
});
var dbPath = Path.Combine(dataPath, "app.db");
builder.Services.AddDbContext<DeskDbContext>(options => options.UseSqlite($"Data Source={dbPath};Foreign Keys=True"));
builder.Services.AddScoped<WorkItemService>();
builder.Services.AddSingleton<IBusinessClock, SystemBusinessClock>();
builder.Services.AddScoped<IWorkbenchService, WorkbenchService>();
builder.Services.AddScoped<IFinanceService, FinanceService>();
builder.Services.AddScoped<IShippingService, ShippingService>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddSingleton<MaintenanceCoordinator>();
builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
builder.Services.AddSingleton<DailyBackupScheduler>();
builder.Services.AddHostedService<DailyBackupScheduler>(provider => provider.GetRequiredService<DailyBackupScheduler>());
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<IWorkspaceSession,WebWorkspaceSession>();
builder.Services.AddScoped<IClientPlatform,WebClientPlatform>();
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(new ClientApiIdentity(dataPath));
builder.Services.AddSingleton<ClientApiSessions>();
builder.Services.AddSingleton<SyncMergePlans>();
builder.Services.AddSingleton<IWorkspaceGateway>(p=>new LocalWorkspaceGateway(p.GetRequiredService<IServiceScopeFactory>()));
const string appUrl = "http://127.0.0.1:5059";
if (!Uri.TryCreate(appUrl, UriKind.Absolute, out var parsedUrl) || parsedUrl.Host is not ("127.0.0.1" or "localhost" or "::1"))
    throw new InvalidOperationException("工作台只允许绑定本机回环地址。");
builder.WebHost.UseUrls(networkAccess.Current.Enabled
    ? new[] { appUrl }.Concat(networkAccess.Addresses.Select(ip => $"http://{ip}:5059")).ToArray() : new[] { appUrl });
var app = builder.Build();
await MigrateWithRecoveryAsync(app.Services, dbPath, dataPath);
StorageLocationGuard.RememberInitialized(dataPath, registrationPath);
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); }
app.UseMiddleware<LocalAccessMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<ClientApiAccessMiddleware>();
app.UseMiddleware<LanAccessMiddleware>();
app.UseMiddleware<MaintenanceRequestMiddleware>();
app.UseAuthorization(); app.UseStaticFiles(); app.MapStaticAssets(); app.MapRazorPages().WithStaticAssets();
app.MapGet("/healthz", async (IServiceProvider services, CancellationToken ct) =>
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
    await db.Database.OpenConnectionAsync(ct);
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
        var ready = Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
        return Results.Json(new { ApplicationId = "PersonalOrderDesk", Ready = ready });
    }
    finally { await db.Database.CloseConnectionAsync(); }
});
app.MapClientApi(dbPath,dataPath);
app.MapSyncMerge(dbPath,dataPath);
app.MapBlazorHub(o=>o.CloseOnAuthenticationExpiration=true);
app.Run();

static async Task MigrateWithRecoveryAsync(IServiceProvider services, string databasePath, string dataDirectory)
{
    string? protection = null;
    if (File.Exists(databasePath))
    {
        protection = Path.Combine(dataDirectory, "Backups", $"migration-protection_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(protection)!);
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await source.OpenAsync();
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = protection, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await target.OpenAsync();
        source.BackupDatabase(target);
        string schema = "";
        await using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
            {
                command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;";
                schema = Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
            }
        }
        await File.WriteAllTextAsync(protection + ".manifest.json", JsonSerializer.Serialize(new
        { ProductId = "PersonalOrderDesk", Schema = schema, CreatedAt = DateTimeOffset.UtcNow }));
    }
    try
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DeskDbContext>().Database.MigrateAsync();
    }
    catch (Exception migrationError)
    {
        if (protection is null) throw;
        SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" }) if (File.Exists(sidecar)) File.Delete(sidecar);
        File.Copy(protection, databasePath, true);
        var logDirectory = Path.Combine(dataDirectory, "Logs"); Directory.CreateDirectory(logDirectory);
        await File.AppendAllTextAsync(Path.Combine(logDirectory, "maintenance.jsonl"), JsonSerializer.Serialize(new
        { at = DateTimeOffset.UtcNow, operation = "migration", success = false, protectionBackupPath = protection, error = migrationError.ToString() }) + Environment.NewLine);
        throw new InvalidOperationException($"数据库升级失败。原数据库已从迁移前保护备份恢复：{protection}", migrationError);
    }
}
