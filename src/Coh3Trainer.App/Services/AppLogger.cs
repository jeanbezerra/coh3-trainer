using System.IO;
using System.Text;

namespace Coh3Trainer.Services;

public interface IAppLogger
{
    void Write(string message);
}

public sealed class FileAppLogger : IAppLogger
{
    private readonly IApplicationPaths _paths;
    private readonly object _syncRoot = new();

    public FileAppLogger(IApplicationPaths paths)
    {
        _paths = paths;
    }

    public void Write(string message)
    {
        try
        {
            lock (_syncRoot)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                var logPath = Path.Combine(_paths.LogsDirectory, $"trainer-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(
                    logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interrupt the trainer workflow.
        }
    }
}
