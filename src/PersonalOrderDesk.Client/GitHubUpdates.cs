using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PersonalOrderDesk.Client;

public sealed record AvailableUpdate(string Version,string Notes,string Page,string FileName,string Url,string Sha256);
public static class GitHubUpdates
{
    public const string Repository="MinMie998/personal-order-desk";
    public static string CurrentVersion=>typeof(GitHubUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"2.0.0-preview.3";
    private sealed record Asset(string Name,[property:JsonPropertyName("browser_download_url")]string Url,string? Digest);
    private sealed record Release([property:JsonPropertyName("tag_name")]string Tag,bool Draft,bool Prerelease,string? Body,[property:JsonPropertyName("html_url")]string Page,Asset[] Assets);
    private static HttpClient Client()
    {
        var http=new HttpClient {Timeout=TimeSpan.FromMinutes(5)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PersonalOrderDesk/2.0");return http;
    }
    public static async Task<AvailableUpdate?> Check(bool android,CancellationToken ct=default)
    {
        using var http=Client();
        var releases=await http.GetFromJsonAsync<Release[]>($"https://api.github.com/repos/{Repository}/releases?per_page=20",ct)??[];
        var release=releases.FirstOrDefault(x=>!x.Draft&&(!x.Prerelease||CurrentVersion.Contains('-'))&&IsNewer(x.Tag,CurrentVersion));
        if(release is null)return null;
        var file=android?"PersonalOrderDesk-android.apk":"PersonalOrderDesk-win-x64.zip";
        var asset=release.Assets.SingleOrDefault(x=>x.Name==file)??throw new InvalidDataException("新版安装包尚未发布完整，请稍后重试。");
        var sums=release.Assets.SingleOrDefault(x=>x.Name=="SHA256SUMS.txt")??throw new InvalidDataException("新版尚未提供校验文件，暂不下载。");
        ValidateUrl(sums.Url);ValidateUrl(asset.Url);
        var checksum=(await http.GetStringAsync(sums.Url,ct)).Split('\n').Select(x=>x.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault(x=>x.Length==2&&x[1].TrimStart('*')==file)?[0];
        if(checksum is null||checksum.Length!=64||!checksum.All(Uri.IsHexDigit))throw new InvalidDataException("更新包校验信息不完整。");
        if(asset.Digest is {} digest&&!digest.Equals("sha256:"+checksum,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新包校验信息不一致。");
        return new(release.Tag,release.Body??"",release.Page,file,asset.Url,checksum);
    }
    public static bool IsNewer(string candidate,string current)
    {
        static (Version Base,string[] Pre)? Parse(string text){var p=text.TrimStart('v').Split('+')[0].Split('-',2);return Version.TryParse(p[0],out var v)?(v,p.Length==2?p[1].Split('.'):[]):null;}
        var a=Parse(candidate);var b=Parse(current);if(a is null||b is null)return false;
        var comparison=a.Value.Base.CompareTo(b.Value.Base);if(comparison!=0)return comparison>0;
        var ap=a.Value.Pre;var bp=b.Value.Pre;if(ap.Length==0||bp.Length==0)return ap.Length==0&&bp.Length>0;
        for(int i=0;i<Math.Min(ap.Length,bp.Length);i++){int n;if(long.TryParse(ap[i],out var an)&&long.TryParse(bp[i],out var bn))n=an.CompareTo(bn);else n=string.CompareOrdinal(ap[i],bp[i]);if(n!=0)return n>0;}return ap.Length>bp.Length;
    }
    private static void ValidateUrl(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https"||u.Host!="github.com"||!u.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal))throw new InvalidDataException("更新来源不是项目的 GitHub 发布页。");
    }
    public static async Task<string> Download(AvailableUpdate update,string directory,CancellationToken ct=default)
    {
        ValidateUrl(update.Url);Directory.CreateDirectory(directory);
        var target=Path.Combine(directory,Path.GetFileName(update.FileName));var temporary=target+".partial";
        try {
            using var http=Client();using var response=await http.GetAsync(update.Url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            await using(var input=await response.Content.ReadAsStreamAsync(ct))await using(var output=File.Create(temporary)){
                var buffer=new byte[81920];long total=0;int n;while((n=await input.ReadAsync(buffer,ct))>0){total+=n;if(total>600L*1024*1024)throw new InvalidDataException("更新包超过大小限制。");await output.WriteAsync(buffer.AsMemory(0,n),ct);}
            }
            await using(var stream=File.OpenRead(temporary))if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(update.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新包校验失败，未打开安装包。");
            File.Move(temporary,target,true);return target;
        } finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
