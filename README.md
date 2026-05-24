# ✦ OptiScaler Client

[![GitHub Release](https://img.shields.io/github/v/release/Agustinm28/Optiscaler-Client?style=flat-square&color=8A2BE2)](https://github.com/Agustinm28/Optiscaler-Client/releases/tag/OptiscalerClient-1.0.5)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/License-GPL--3.0--or--later-yellow.svg?style=flat-square)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4?style=flat-square&logo=windows)](https://www.microsoft.com/windows)
[![Platform: Linux](https://img.shields.io/badge/Platform-Linux-E95420?style=flat-square&logo=linux)](https://www.linux.org)

> **⚠️ Disclaimer:** This is **not** an official OptiScaler project. I am not affiliated with the OptiScaler team. This is a personal project developed without any commercial purpose. Anyone is free to try and use this software at their own risk.

**OptiScaler Client** is a modern, high-performance desktop utility designed to simplify the installation, management, and update of the **OptiScaler** mod across your entire game library. Built with **C#** and **Avalonia UI**.

---

## Screenshots

* Main window

<img width="1920" height="1032" alt="1 0 4_A" src="https://github.com/user-attachments/assets/f39de984-a055-41ef-8900-3ee4e4317a68" />

* Game management

<img width="1140" height="600" alt="oc_01" src="https://github.com/user-attachments/assets/f8608451-d18e-410f-9df5-5e63a27e0e02" />

* Game management after installation

<img width="1140" height="621" alt="oc_02" src="https://github.com/user-attachments/assets/cd8e41fc-a6e0-4ca3-a035-ebc8fd4d32bb" />

---

## 🚀 Key Features

### Game Discovery

- **Multi-Platform Auto-Scanner** — Scans **Steam, Epic Games, GOG, EA, Ubisoft, Battle.net, Heroic, and Xbox/Microsoft Store** libraries in parallel. On Linux, only **Steam** and **Heroic** (Epic Games, GOG) are scanned automatically.
- **Custom Folder Scanning** — Add any folder as a scan source for DRM-free or standalone games.
- **Manual Game Addition** — Add games by selecting the executable directly.
- **Drive Root Filtering** — Limit scanning to specific drives.
- **Smart Exclusions** — Pre-configured exclusions for non-game entries (e.g., Wallpaper Engine, Steamworks Redistributables).
- **Cover Art Fetching** — Automatically fetches game cover art from Steam API and SteamGridDB with local caching.

### Installation & Uninstallation

- **Quick Install / Uninstall** — One-click toggle per game directly from the main view. Automatically downloads components if not cached.
- **Auto Install** — Detects game directory structure automatically, including **UE5/Phoenix** game layouts.
- **Manual Install** — Select the target executable manually for non-standard game structures.
- **Bulk Install** — Install OptiScaler across multiple games at once with platform filtering, component selection, and profile application.
- **Injection Method Selection** — Choose the DLL injection method: `dxgi.dll`, `winmm.dll`, `d3d12.dll`, `dbghelp.dll`, `version.dll`, `wininet.dll`, `winhttp.dll`.
- **Backup & Restore** — Original game files are backed up before installation and restored on uninstall.

### Component Management

- **OptiScaler** — Core upscaling mod with stable and beta version channels.
- **Fakenvapi** — Compatibility layer for **AMD/Intel GPUs**, installed alongside OptiScaler when needed.
- **Nukem's DLSSG-to-FSR3** — Frame generation bridge that converts DLSS Frame Gen to FSR3.
- **FSR 4 INT8 Extras** — INT8 shader injection for non-RDNA 4 GPUs.
- **OptiPatcher** — ASI plugin loader, automatically configured with `LoadAsiPlugins=true` in OptiScaler.ini.

### Profiles

- **OptiScaler Profiles** — Create, edit, clone, and manage INI-based configuration profiles.
- **Easy Mode Editor** — Simple toggle-based interface for common settings.
- **Advanced Mode Editor** — Full section-based settings editor with search and sidebar navigation.
- **Default Profile** — Set a default profile that is applied automatically during Quick Install and Bulk Install.
- **Built-in Default** — "OptiScaler Standard" profile ships out-of-the-box with sensible defaults.

## Network & Proxy

- Supports system proxy settings and `HTTP_PROXY` / `HTTPS_PROXY` environment variables.
- Also supports explicit proxy configuration from app settings (including auth when required).
- Network settings are persisted in app configuration.

### Settings & Customization

- **Default Versions** — Configure default OptiScaler, Extras, and OptiPatcher versions for Quick Install.
- **Beta Channel Toggle** — Show or hide beta versions in all version selectors.
- **GPU Detection** — Automatically detects installed GPUs with platform-specific providers and discrete GPU preference logic.
- **Preferred GPU Selection** — Choose which GPU is used for installation decisions.
- **Scan Source Management** — Enable/disable per-platform scanners and configure custom folders.
- **Cache Management** — View and delete cached OptiScaler and Extras versions to free storage.
- **SteamGridDB Integration** — Optional API key for improved cover art fetching.
- **Clear Application Cache** — Full reset: delete all stored data (games, covers, config, analysis cache).

### UI & UX

- **List & Grid Views** — Switch between compact list and card-based grid layouts (preference saved).
- **Real-Time Search** — Filter games by name as you type.
- **Edit Mode** — Reorder games via drag-and-drop or arrow buttons; hide/show games.
- **Technology Badges** — Visual indicators showing detected DLSS, FSR, XeSS, DLSS Frame Gen versions.
- **Platform Badges** — Icons for each supported game platform.
- **Toast Notifications** — Non-blocking notifications with progress bars for downloads and operations.
- **Status Bar** — Footer with real-time operation feedback and GPU info.
- **Loading Overlays** — Animated indicators during scanning and startup checks.
- **Window State Persistence** — Window size, position, and maximized state are saved across sessions.
- **Configurable Animations** — UI transitions can be disabled in Settings for performance.

### Localization

Full interface translation in **14 languages**:

| Language | Language |
|---|---|
| 🇬🇧 English | 🇯🇵 Japanese |
| 🇪🇸 Spanish | 🇰🇷 Korean |
| 🇩🇪 German | 🇳🇱 Dutch |
| 🇫🇷 French | 🇵🇱 Polish |
| 🇮🇹 Italian | 🇷🇺 Russian |
| 🇧🇷 Portuguese (Brazil) | 🇹🇷 Turkish |
| 🇨🇳 Chinese (Simplified) | 🇹🇼 Chinese (Traditional) |

---

## 📖 Usage Guide

### Getting Started

1. **Find your games** — Click **"Scan Games"** to automatically detect installed titles from all supported platforms. You can manage scan sources or add custom folders in **Settings**. For standalone games, use **"Add Manually"**.
2. **Select a Game** — Click **"Manage"** next to any game, or use **Quick Install** for a one-click experience.
3. **Install OptiScaler** — From the Manage window, choose version, injection method, components, and profile, then click **"Auto Install"**. Or just hit **Quick Install** from the main view to install with your configured defaults.
4. **Bulk Install** — Use the **"Bulk Install"** button to install OptiScaler on multiple games simultaneously.
5. **Launch & Tweak** — Start your game normally. Press **`Insert`** to open the OptiScaler in-game menu and adjust upscaling settings in real-time.

### Profiles

1. Navigate to the **Profiles** tab in the sidebar.
2. Click **"New Profile"** to create a custom configuration.
3. Use **Easy Mode** for quick toggles or **Advanced Mode** for full INI control.
4. Set a default profile in **Settings → Manage Default Versions** so it's applied automatically during Quick Install.

### Uninstalling

- **Quick Uninstall** — Click the Quick Install button on any game that already has OptiScaler installed.
- **Manage → Uninstall** — Open the game management window and click **Uninstall**.
- Both methods will restore original game files from backup and clean up all OptiScaler artifacts.

---

## 🛠️ Installation & Requirements

### Platform Support

- Windows
- Linux

### Instructions

1. Download the latest release asset from [Releases](https://github.com/Agustinm28/Optiscaler-Client/releases).
2. Extract the package.
3. Run `OptiscalerClient.exe`.

### Notes

- The app is self-contained, so no external .NET runtime installation is required.
- On Linux, automatic scanner sources are focused on Steam and Heroic libraries.
- Manual add/install flows currently target executable files (`.exe`) for game selection.
- **Nix/NixOS:** A `flake.nix` is provided for building via Nix. Before the first build, regenerate the lock file by running `nix flake update` in the project root.

---

## 🛡️ Security & Antivirus False Positives

**Is this software safe?**

Yes, OptiScaler Client is completely safe and open-source. However, some antivirus programs may flag it as suspicious due to **false positive detections**.

### Why does this happen?

- **File Downloads**: The app downloads `.zip` and `.dll` files from GitHub (OptiScaler, Fakenvapi, NukemFG)
- **Heuristic Detection**: Antivirus software may flag download behavior as "potentially unwanted"
- **Unsigned Binary**: The executable is not digitally signed (code signing certificates cost $100-300/year)

### Common False Positives

- **Zillya**: `Downloader.MLoki.Win64.10` — Known for aggressive heuristics
- **Other AVs**: May show generic "downloader" or "trojan" warnings

### What you can do

1. **Verify the Source**: Download only from official [GitHub Releases](https://github.com/Agustinm28/Optiscaler-Client/releases)
2. **Check VirusTotal**: Upload the file to [VirusTotal.com](https://www.virustotal.com) — most reputable AVs will show clean
3. **Review the Code**: This is open-source — you can inspect all code before running
4. **Add Exception**: Whitelist `OptiscalerClient.exe` in your antivirus settings

### Transparency

All downloads are from official sources:
- OptiScaler: `github.com/optiscaler/OptiScaler`
- Fakenvapi: `github.com/optiscaler/fakenvapi`
- NukemFG: `github.com/Nukem9/dlssg-to-fsr3`

The application **never** collects personal data, connects to third-party servers, or performs any malicious actions. All source code is available for audit.

---

## 🤝 Contributing

We welcome contributions! If you'd like to improve OptiScaler Client:

1. **Fork** the project.
2. Create your **Feature Branch** (`git checkout -b feature/AmazingFeature`).
3. **Commit** your changes (`git commit -m 'Add some AmazingFeature'`).
4. **Push** to the branch (`git push origin feature/AmazingFeature`).
5. Open a **Pull Request**.

---

## 📄 License & Acknowledgments

### License

**OptiScaler Client** is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License** as published by the Free Software Foundation, either **version 3** of the License, or (at your option) **any later version**.

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**; without even the implied warranty of **MERCHANTABILITY** or **FITNESS FOR A PARTICULAR PURPOSE**. See the [GNU General Public License](LICENSE) for more details.

**Copyright (C) 2026 Agustín Montaña (Agustinm28)**

### Acknowledgments & Third-Party Software

- **Special thanks and deep respect to the OptiScaler development team** for creating and maintaining this incredible software that enhances gaming experiences for countless users worldwide.
- **[OptiScaler](https://github.com/optiscaler/OptiScaler)**: The core upscaling technology that makes this possible.
- **[fakenvapi](https://github.com/optiscaler/fakenvapi)**: Essential compatibility layer developed by the OptiScaler team.
- **[OptiPatcher](https://github.com/optiscaler/OptiPatcher)**: ASI plugin loader by the OptiScaler team.
- **[NukemFG (DLSSG-to-FSR3)](https://github.com/Nukem9/dlssg-to-fsr3)**: Frame Generation bridge by Nukem.

This client application is merely a frontend interface to help users more easily manage and install the amazing work done by the OptiScaler team and other contributors. While OptiScaler Client itself is licensed under GPL-3.0-or-later, the third-party components it downloads and manages may be subject to their own respective licenses.

---

<p align="center">
  Developed with ❤️
</p>
