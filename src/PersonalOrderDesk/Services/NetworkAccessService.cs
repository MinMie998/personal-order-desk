using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace PersonalOrderDesk.Services;

public sealed record NetworkAccessConfiguration(bool Enabled = false, string? PasswordHash = null, string? Revision = null);

public sealed class NetworkAccessService
{
    private readonly string path;
    private readonly PasswordHasher<string> hasher = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Since)> failures = new();
    public NetworkAccessConfiguration Current { get; }
    public IReadOnlyList<string> Addresses { get; }
    public NetworkAccessService(string dataDirectory, IEnumerable<string>? addresses = null)
    {
        path = Path.Combine(dataDirectory, "network-access.json");
        Current = Read();
        if (Current.Enabled && (string.IsNullOrEmpty(Current.PasswordHash) || string.IsNullOrEmpty(Current.Revision)))
            throw new InvalidOperationException("局域网配置不完整，已停止启动。请在本机关闭局域网访问或重新设置密码。");
        Addresses = (addresses ?? NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString()))
            .Where(a => IPAddress.TryParse(a, out var ip) && IsPrivateAddress(ip)).Distinct().ToArray();
    }

    public NetworkAccessConfiguration Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<NetworkAccessConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException("局域网配置为空。")
        : new();

    public void Save(bool enabled, string? password, string? confirmation)
    {
        lock (gate)
        {
            var old = Read();
            if (!string.IsNullOrEmpty(password) && (password.Length < 10 || password.Length > 128 || password != confirmation))
                throw new ArgumentException("密码需为10至128个字符，两次输入必须一致。");
            if (enabled && string.IsNullOrEmpty(password) && string.IsNullOrEmpty(old.PasswordHash))
                throw new ArgumentException("首次开启局域网访问时，请先设置登录密码。");
            var next = new NetworkAccessConfiguration(enabled,
                string.IsNullOrEmpty(password) ? old.PasswordHash : hasher.HashPassword("owner", password),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, true);
        }
    }

    public bool VerifyPassword(string remoteAddress, string password)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (failures.TryGetValue(remoteAddress, out var value) && now - value.Since < TimeSpan.FromMinutes(15) && value.Count >= 5) return false;
            if (Current.Enabled && Current.PasswordHash is not null && password.Length <= 128
                && hasher.VerifyHashedPassword("owner", Current.PasswordHash, password) != PasswordVerificationResult.Failed)
            { failures.Remove(remoteAddress); return true; }
            failures[remoteAddress] = now - value.Since >= TimeSpan.FromMinutes(15) ? (1, now) : (value.Count + 1, value.Since);
            return false;
        }
    }

    public static bool IsLocal(HttpContext context) => context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }
}
