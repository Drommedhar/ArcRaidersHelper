using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace OverlayApp.Infrastructure;

internal sealed class GlobalHotkeyManager : IDisposable
{
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private bool _isDisposed;

    public GlobalHotkeyManager(HwndSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _handle = source.Handle;
        _source.AddHook(WndProc);
    }

    public void Register(int id, ModifierKeys modifiers, Key key, Action callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        if (_callbacks.ContainsKey(id))
        {
            Unregister(id);
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var modifierValue = (uint)modifiers;

        if (!NativeMethods.RegisterHotKey(_handle, id, modifierValue, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to register hotkey {modifiers}+{key} (error {error}).");
        }

        _callbacks[id] = callback;
    }

    public void Clear()
    {
        foreach (var hotkeyId in _callbacks.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_handle, hotkeyId);
        }

        _callbacks.Clear();
    }

    private void Unregister(int id)
    {
        if (_callbacks.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_handle, id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();
            if (_callbacks.TryGetValue(hotkeyId, out var action))
            {
                action();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Clear();
        _source.RemoveHook(WndProc);
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static class NativeMethods
    {
        internal const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
