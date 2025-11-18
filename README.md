# Arc Raiders Overlay Helper

A lightweight WPF utility that exposes [arctracker.io](https://arctracker.io) as an always-on-top overlay so you can access the live loadout/encounter data without alt-tabbing out of your DirectX game. The app embeds the site inside WebView2, registers global hotkeys, and stays hidden until you summon it.

- **Global hotkey toggle** (`Ctrl+Alt+O` by default) to show or hide the overlay while the game is focused.
- **Secondary hotkeys** for kill-switch (`Ctrl+Alt+Shift+O`) and click-through toggle (`Ctrl+Alt+T`).
- **Built-in WebView2** with navigation interception so popups stay inside the same surface.
- **Fully transparent overlay frame** with per-pixel transparency so you only see the tracker content floating above your game.
- **Instant click-through mode** (button + hotkey) that removes the header chrome, disables hit-testing, and lets the game capture mouse/keyboard while the tracker stays visible.
- **Custom CSS injection** keeps arctracker.io transparent and applies the same opacity settings to the site content, so the overlay always blends with the game.
- **Optional ad filtering** (toggle in Settings) adds lightweight network filtering + CSS so the tracker stays clean without extra browser extensions.
- **Settings menu** (header button) to tweak hotkeys, default click-through state, opacity, topmost behavior, ad filtering, and launch visibility without editing JSON.
- **Automatic persistence** of the window size/position, maximized state, click-through preference, opacity, URL, and custom hotkeys between sessions.

## Requirements
- Windows 10 20H1 (build 19041) or newer.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- [Microsoft Edge WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (installed on most modern Windows builds by default).

## Quick start
```powershell
cd d:\git\ArcRaidersHelper
dotnet run --project OverlayApp
```
The window appears once at launch, applies your last saved placement, initializes WebView2, and either hides or stays visible based on your preference. Use `Ctrl+Alt+O` to bring it back on top of your game session and `Esc` to send it back into the background. When the overlay is visible you can interact with arctracker.io exactly as you would in a browser, including mouse input and text entry. Switch the surface into click-through mode with the header toggle button or `Ctrl+Alt+T` whenever you need to keep it visible while sending inputs to the game.

### Settings UI
- Click the **Settings** button in the title bar to open an in-app modal dialog.
- Adjust default click-through behavior, independent opacity sliders (normal vs click-through), ad filtering, or hotkey strings (`Ctrl+Alt+T` style format) and hit **Apply** to persist. The opacity sliders drive both the glass chrome and the embedded website thanks to injected CSS.
- Enable **Hide ads on tracker** if you want the embedded page to block common ad hosts and DOM containers without modifying the upstream site; toggle it off if anything legitimate gets caught.
- Invalid URLs or duplicate hotkeys are rejected with a warning so you do not end up with unusable shortcuts.

### Customizing hotkeys and placement
User preferences live in `%APPDATA%\ArcRaidersHelper\settings.json`. You can edit this file (while the app is closed) to change hotkey combos or seed a default window size/position. Unknown or invalid hotkeys automatically fall back to the defaults listed above, and the overlay automatically remembers whichever language you last selected on arctracker.io.

## Project structure
- `ArcRaidersHelper.sln` – solution container.
- `OverlayApp/` – WPF app with `MainWindow` (overlay UI), `Infrastructure/GlobalHotkeyManager.cs` for Win32 hotkeys, `Infrastructure/UserSettings*.cs` for persistence and hotkey parsing, and `SettingsWindow`/`ViewModels` for the in-app configuration surface.

## Next steps
- Add preset hotkeys for navigating between tracker tabs or refreshing key panels.
- Make the settings dialog support presets/profiles for multiple tracker URLs or custom CSS overlays.
