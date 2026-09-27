using System.Windows;
using Coh3Trainer.Localization;
using Coh3Trainer.Services;

namespace Coh3Trainer;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Coh3ResourceTrainer";
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            var settings = new SettingsStore().Load();
            var localizer = LocalizationService.Current;
            localizer.SetCulture(settings.Culture);
            MessageBox.Show(
                localizer.Get("App.AlreadyRunning"),
                localizer.Get("App.AlreadyRunningTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
