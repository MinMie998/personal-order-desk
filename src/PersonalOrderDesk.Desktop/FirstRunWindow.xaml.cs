using System.IO;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Storage;
namespace PersonalOrderDesk.Desktop;
public partial class FirstRunWindow : Window
{
    public string DataDirectory {get;private set;}="";
    public FirstRunWindow(string recommended){InitializeComponent();Folder.Text=recommended;}
    private void Browse(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog {Title="选择账本数据文件夹"};if(dialog.ShowDialog(this)==true)Folder.Text=dialog.FolderName;}
    private void Confirm(object sender,RoutedEventArgs e)
    {
        try{
            var folder=Folder.Text.Trim();if(!Path.IsPathFullyQualified(folder))throw new ArgumentException("请选择完整的数据文件夹路径。");folder=Path.GetFullPath(folder);
            if(folder.StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请把数据放在程序目录外，方便以后升级。");
            StorageLocationGuard.Validate(folder,Path.Combine(folder,"storage-location.json"));
            var database=Path.Combine(folder,"app.db");
            if(File.Exists(database)){
                using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();
                using var command=connection.CreateCommand();command.CommandText="PRAGMA integrity_check";if(Convert.ToString(command.ExecuteScalar())!="ok")throw new InvalidDataException("原账本完整性检查未通过，请保留文件并检查备份。");
                using var context=new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);
                var applied=context.Database.GetAppliedMigrations().ToArray();var known=context.Database.GetMigrations().ToArray();if(applied.Length==0||applied.Any(x=>!known.Contains(x)))throw new InvalidDataException("这不是受支持的工作台账本，或账本版本比程序更新。");
            }
            Directory.CreateDirectory(folder);var probe=Path.Combine(folder,".desk-write-"+Guid.NewGuid().ToString("N"));using(File.Create(probe)){}File.Delete(probe);
            DataDirectory=folder;DialogResult=true;
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException){Error.Text=ex.Message;}
    }
}
