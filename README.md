# Arc Raiders Overlay Helper

A lightweight, native WPF overlay for Arc Raiders that helps you track your progress, quests, and needed items directly over the game.

## Features

- **Native Overlay**: A high-performance, always-on-top overlay that blends seamlessly with your game.
- **Progress Tracking**: Track your Quests, Hideout upgrades, and Projects.
  - **Automatic tracking**: All these plus needed items can be automatically tracked. This feature is still experimental and was only tested on **Borderless Window** and **2560x1440 resolution**!
- **Needed Items**: Quickly see what items you need to collect for your active goals.
- **Interactive & Click-through**:
  - **Interactive Mode**: Browse data, manage settings, and check your progress.
  - **Click-through Mode**: Toggle (`Ctrl+Alt+T`) to make the overlay transparent to mouse clicks, allowing you to play while keeping your data visible. In this mode, the navigation bar is hidden to minimize screen real estate.
- **Customizable**:
  - Adjust opacity for both interactive and click-through modes.
  - Toggle "Always on Top".
  - Configurable global hotkeys.
  - Window position and size persistence.
- **Auto-Updates**: Automatically checks for and installs the latest version on startup.

## Data & Attribution

This project uses data provided by the community.

- **Data Source**: [RaidTheory/arcraiders-data](https://github.com/RaidTheory/arcraiders-data)
- **Inspired by**: [arctracker.io](https://arctracker.io)

All game content, including but not limited to game mechanics, items, names, and imagery, is copyright © Embark Studios AB. This repository is a community resource and is not affiliated with or endorsed by Embark Studios AB.

## Quick Start

1. Download the latest release.
2. Run `OverlayApp.exe`.
3. Use `Ctrl+Alt+O` to toggle the overlay visibility.
4. Use `Ctrl+Alt+T` to toggle click-through mode.

## Requirements

- Windows 10/11 (Build 19041 or newer)
- .NET 8 Desktop Runtime

## Development

```powershell
git clone https://github.com/Drommedhar/ArcRaidersHelper.git
cd ArcRaidersHelper
dotnet run --project OverlayApp
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
