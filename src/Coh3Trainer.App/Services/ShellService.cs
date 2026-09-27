using System.Diagnostics;
using System.IO;

namespace Coh3Trainer.Services;

public interface IShellService
{
    void OpenFolder(string path);
}

public sealed class WindowsShellService : IShellService
{
    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
