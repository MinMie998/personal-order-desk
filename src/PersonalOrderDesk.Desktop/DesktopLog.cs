using Microsoft.Extensions.Logging;
namespace PersonalOrderDesk.Desktop;
public sealed class DesktopLog(string path) : ILoggerProvider
{
    private readonly object gate=new();
    private readonly string filePath=path;
    public ILogger CreateLogger(string categoryName)=>new FileLog(this,categoryName);
    public void Dispose(){}
    private sealed class FileLog(DesktopLog owner,string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Warning;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> format)
        {
            if(!IsEnabled(level))return;
            lock(owner.gate){System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(owner.filePath)!);System.IO.File.AppendAllText(owner.filePath,$"{DateTimeOffset.Now:O} {category}: {format(state,exception)} {exception}\n");}
        }
    }
}




