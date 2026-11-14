using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Desktop;
public sealed class WindowsClientPlatform : IClientPlatform
{
    public Task OpenUpdate(string path){Process.Start(new ProcessStartInfo(path){UseShellExecute=true});return Task.CompletedTask;}
    public async Task CopyText(string text)=>await Application.Current.Dispatcher.InvokeAsync(()=>Clipboard.SetText(text));
    public Task OpenWeb(string path){Process.Start(new ProcessStartInfo("http://127.0.0.1:5059/"+path.TrimStart('/')){UseShellExecute=true});return Task.CompletedTask;}
    public async Task ShareFile(string path)=>await Application.Current.Dispatcher.InvokeAsync(()=>{var dialog=new SaveFileDialog{FileName=System.IO.Path.GetFileName(path),Filter=System.IO.Path.GetExtension(path)==".zip"?"同步保护备份|*.zip":"工作台备份|*.db"};if(dialog.ShowDialog()==true){System.IO.File.Copy(path,dialog.FileName,true);if(System.IO.File.Exists(path+".manifest.json"))System.IO.File.Copy(path+".manifest.json",dialog.FileName+".manifest.json",true);}});
}
public sealed class WindowsSecretStore(string root) : IClientSecretStore
{
    private string PathFor(string key)=>System.IO.Path.Combine(root,"secrets",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".bin");
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("PersonalOrderDesk.Client.v1");
    public async Task<string?> Read(string key){var path=PathFor(key);if(!System.IO.File.Exists(path))return null;try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(await System.IO.File.ReadAllBytesAsync(path),Entropy,DataProtectionScope.CurrentUser));}catch(CryptographicException){return null;}}
    public async Task Save(string key,string value){var path=PathFor(key);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);await System.IO.File.WriteAllBytesAsync(path,ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Entropy,DataProtectionScope.CurrentUser));}
}

