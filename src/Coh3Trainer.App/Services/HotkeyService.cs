using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using Coh3Trainer.Interop;
using Coh3Trainer.Localization;
using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private readonly ITextLocalizer _localizer;
    private readonly Dictionary<int, ResourceKind> _registrations = new();

    public HotkeyService(nint windowHandle, ITextLocalizer? localizer = null)
    {
        _windowHandle = windowHandle;
        _localizer = localizer ?? LocalizationService.Current;
        _source = HwndSource.FromHwnd(windowHandle) ??
                  throw new InvalidOperationException(_localizer.Get("Hotkey.WindowUnavailable"));
        _source.AddHook(WindowProc);
    }

    public event EventHandler<ResourceKind>? Pressed;

    public IReadOnlyList<string> Register(TrainerSettings settings)
    {
        UnregisterAll();
        var errors = new List<string>();
        var id = 7000;

        foreach (var kind in TrainerSettings.EnabledResources)
        {
            var keyName = settings.Resources[kind].Hotkey;
            if (!Enum.TryParse<Key>(keyName, true, out var key))
            {
                errors.Add(_localizer.Get("Hotkey.Invalid", ResourceName(kind), keyName));
                continue;
            }

            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (!NativeMethods.RegisterHotKey(_windowHandle, id, NativeMethods.ModNoRepeat, virtualKey))
            {
                errors.Add(_localizer.Get("Hotkey.InUse", keyName));
                continue;
            }

            _registrations[id] = kind;
            id++;
        }

        return errors;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _registrations.TryGetValue(wParam.ToInt32(), out var resource))
        {
            handled = true;
            Pressed?.Invoke(this, resource);
        }

        return nint.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registrations.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }
        _registrations.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WindowProc);
    }

    private string ResourceName(ResourceKind kind) => _localizer.Get($"Resource.{kind}");
}
