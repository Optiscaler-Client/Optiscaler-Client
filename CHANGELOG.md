#### [1.0.7.1] ####

# What’s Changed

This patch focuses on **installation reliability**. It fixes custom OptiScaler imports, corrects two path bugs that placed the `plugins` folder and FSR 4 DLL swaps outside the `OptiScaler` folder on nightly builds, and resolves a **dxgi/XeSS conflict** that crashed games before they opened. Streamline downloads and the FP8 version selector no longer break when a GitHub request fails. On Linux, an installation path issue that silently downgraded **FSR 4 to FSR 3** has been fixed. This release also adds multipliers up to **x6** for **Intel Xe Frame Generation**.

# Changelog

### Fixes

- Fixed a bug where installing a custom OptiScaler version failed because the installer still looked for a release to download, which does not exist for custom imports.
- Fixed a bug where the `plugins` folder was created outside the `OptiScaler` folder when installing nightly versions.
- Fixed a bug where FSR 4 DLL swaps were applied outside the `OptiScaler` folder when installing nightly versions.
- Fixed a bug where installing OptiScaler with the **dxgi** injection method interfered with XeSS, crashing the game before it opened.
- Fixed a bug where the FP8 version selector in **FSR 4 DLL Swap** appeared locked when the request to GitHub failed.
- Fixed a bug where downloading Streamline failed due to an error retrieving repository data from GitHub.
- Fixed a bug on Linux where the OptiScaler folder was created under a name OptiScaler itself does not look for, causing **FSR 4 to silently fall back to FSR 3**.

### New

- Added multiplier options up to **x6** alongside **Intel Xe Frame Generation**.

##### Improvements

- Improved GPU model detection on Linux to display the exact commercial name.

#### [1.0.7] ####

# What’s Changed

This update introduces a major overhaul with comprehensive FSR 4 DLL Swapping & Imports (supporting INT8, FP8, and custom DLLs without requiring an OptiScaler install) alongside direct Frame Generation and Multi Frame Generation (MFG) support with multipliers up to x6, DLSS Enabler compatibility, and automated Streamline handling. The Manage window has also been heavily upgraded with direct controls for output upscaler technologies, quality presets, GPU spoofing, quick .ini previews, and support for OptiScaler Nightly builds (thanks to @FelipeGFA). Additionally, users can test experimental integrations for RenoDX and Nvidia Neural Rendering mod for AMD.

Behind the scenes, we have improved game-specific recommended configurations with auto injection selection and Luma UE support, refined FSR 4 behavior on RDNA 3, and boosted overall UI startup performance. Finally, critical stability and interface issues have been addressed, including a new software rendering fallback for older Windows 10 startup crashes, profile changes no longer discarding on exit, and various fixes for touch, high-DPI scaling, and Linux GPU badges.

# Changelog

##### Added

- **FSR 4 DLL Swapping & Imports**
  - Replaced the FSR 4 INT8 section with a comprehensive FSR 4 DLL Swap feature, supporting both INT8 and FP8 versions. You can now swap game DLLs without installing OptiScaler by selecting "None" in the OptiScaler selector and choosing an FSR 4 DLL version.
  - Added the ability to choose which specific files to copy/replace for multi-file FSR 4 releases (e.g., upscaler, loader, radiance cache, frame generation, denoiser).
  - Added support for importing custom FSR 4 DLL versions (both INT8 and FP8).
- **Frame Generation & Multi Frame Generation (MFG)**
  - Integrated direct support for Frame Generation and Multi Frame Generation within the Manage window.
  - Introduced a simple configuration mode where you only need to select the FG technology and multiplier (x3 to x6 available for DLSS-G output). The system automatically handles path configuration, `nvngx` replacement, and Streamline retrieval.
  - Added an advanced configuration mode to select the FG path (e.g., DLSS-G via Streamline, OptiFG), manually choose the `nvngx` replacement, and select the Streamline version to download.
  - Included support for DLSS Enabler to activate Multi Frame Generation. Since DLSS Enabler cannot be directly distributed, versions are fetched from a manually maintained mirror. A custom version import option was also added to ensure updates can be applied if the mirror is outdated.
  - Streamline is automatically downloaded and installed when using multipliers greater than x2 or when DLSS Enabler is used.
- **Manage Window Enhancements**
  - Added an "Output Upscaler" selector to quickly choose the upscaling technology directly from the Manage window (Default, FSR 2, FSR 3, FSR 4, XeSS, DLSS) without needing to navigate to Profiles.
  - Added an "Upscaling Quality" selector to override the upscaler input resolution directly from the Manage window (Game controlled, Native AA, Ultra Quality, Quality, Balanced, Performance, Ultra Performance, Custom).
  - Added a toggle for GPU spoofing with an "Auto" default value.
  - Added a quick preview icon next to the Profiles tooltip to visualize and edit the final `.ini` configuration after applying.
- **OptiScaler Nightly Builds**
  - Added support for downloading and installing OptiScaler "nightly" versions, which are currently required for seamless Multi Frame Generation support.
- **Experimental Features**
  - Introduced an "Experimental" section in Settings to enable third-party integrations:
    - Support for RenoDx Addons, automatically fetching per-game addons to be used with ReShade. A verification step checks if ReShade is installed and redirects to the official page if manual installation is needed.
    - Support for AMD Neural Rendering in two variants: danielblnc's DLSS 5 neural rendering mod for AMD, and MatheusGViana's OptiScaler wrapper. The system automates the process but requires the user to provide the NVIDIA neural rendering DLL once. It also utilizes the original mod to generate necessary weights locally to comply with distribution policies. Experimental Linux support for a danielblnc's fork is included.
- **Settings & Help Updates**
  - Added a "Default Versions & Quick Install Settings" section to configure default values for the Manage window and the Quick Install button.
  - Updated the Help section to explain color codes for different badges, including states where DLLs are merely swapped without OptiScaler installed.

##### Improvements

- **Recommended Configurations & Compatibility**
  - The recommended configuration section now fetches individual settings from the game-specific compatibility page and includes a direct hyperlink.
  - The system now automatically selects the most suitable injection method based on user testing data from the compatibility list.
  - The recommended configurations now also cover Luma Unreal Engine game compatibility lists.
  - The recommended configuration sidebar can now be collapsed.
- **FSR 4 & RDNA 3 Enhancements**
  - The Help documentation was updated to clarify that FSR 4 is supported on RDNA 3 and selecting an INT8 option is no longer required.
  - The system no longer attempts to automatically inject the INT8 DLL on RDNA 3 GPUs, assuming usage of the driver-included version.
- **UI & Performance**
  - Implemented UI and responsiveness improvements across the application, particularly in the Manage window.
  - Improved data fetching speed and efficiency during application startup.
  - Updating a simple configuration no longer requires a full reinstallation; the system will offer to only update the specific setting.
  - The "Manage Local Versions" window now displays the total size of cached files, making it easier to identify data to clear.

##### Fixes

- **UI & Window Behavior**
  - Fixed a bug where leaving the Profile window after making changes would discard them without saving.
  - Fixed an issue where the game cover update modal incorrectly displayed the injection method selector in certain cases.
  - Fixed a bug where non-FSR technology badges were missing in list mode.
  - Fixed an issue where success toasts appeared off-screen or misaligned.
  - Fixed window dragging functionality for touch devices.
  - Fixed modal scaling issues on very high-resolution displays (e.g., handheld consoles).
  - Fixed the Linux GPU display badge, preventing the GPU name from appearing stretched or broken due to inaccuracies.
- **Core Functionality & Stability**
  - Addressed an "Unknown Hard Error on startup" affecting older Windows 10 distributions by introducing a Software rendering mode, which triggers automatically upon repeated crashes or can be manually enabled in Settings.
  - Fixed a bug where one of the FSR 4 DLL keys would disappear after reinstalling with a different profile.

##### New Contributors

- @FelipeGFA made their first contribution with support for OptiScaler nightly builds.

#### [1.0.6.1] ####

# What’s Changed

This update brings several bug fixes, including improvements to manually added games, profile parameters, **FSR 4 INT8 on RDNA2**, and NukemFG detection. It also adds a new **Check for Updates** option and clarifies app profile behavior. Finally, a known **FSR 4.1.1 RDNA2 driver regression** is documented, with **4.0.2c** recommended as a workaround.

# Changelog

### Fixes

- Fixed a bug where the remove game icon disappeared for games that had been previously added manually.
- Fixed a bug where manually added games were no longer recognized as such after an update.
- Fixed a bug where modifying a non-existing parameter in a profile would not add it to the profile.
- Fixed a bug where, after updating the client from 1.0.5 to 1.0.6, games would no longer launch on RDNA2 GPUs using FSR4 INT8 4.0.2c.
- Fixed a bug where NukemFG (DLSSG-to-FSR3) was automatically enabled even in games without native DLSS Frame Generation support, causing OptiScaler to prompt the user to "enable Frame Generation" for an option the game does not actually have.

### New

- Added a **Check for Updates** button under Help to manually check for a new version.
- Clarified that app profiles are custom `.ini` files.

### Known Issues

- FSR4 INT8 4.1.1 may cause black screens, ghosting, or stuttering on RDNA2 GPUs (Radeon RX 6000 and APUs based on this architecture). This is an **AMD driver regression**, not a bug in this app or OptiScaler, and there is currently no ETA for a fix from AMD. **Workaround:** Use FSR4 INT8 version 4.0.2c instead of 4.1.1 on RDNA2 GPUs.

#### [1.0.6] ####

# What’s Changed

This update introduces highly requested features, including **Heroic Games Launcher support**, experimental **Gamepad support** for both Windows and Linux, and direct integration with the **OptiScaler compatibility list** for recommended per-game settings. The game management experience has been significantly improved with **real-time filtering** and a new **favorites system**. Additionally, this release brings important fixes for preserving settings during updates, robust Steam file parsing, and experimental **FSR 4.1.1 support for non-RDNA4 GPUs**.

# Changelog

## Added

- **Platform & Launcher Integrations**
  - **Heroic Games Launcher Support**: Compatibility and integration for Heroic Games Launcher have been added (by @misterj05).
  - **AUR Support**: Added Arch User Repository (AUR) support for Linux users.
  - **Nix Support**: A Nix `devShell` and build derivation have been implemented (by @jorikvanveen).
- **Controller Support**
  - **Gamepad Support (Beta)**: Added experimental gamepad navigation support for both Windows and Linux. Controls can be found in the Help view. *(Note: This is currently in beta, so some interactions may not feel completely fluid. Feedback and bug reports are welcome!)*
- **Game Management & Filtering**
  - **Advanced Filtering & Favorites**: Three new filters have been added to the Games view, and games can now be marked as favorites.
  - **Real-time Filtering**: Game filtering can now be performed in real-time without needing to scan from scratch.
  - **OptiScaler Compatibility Integration**: A new section has been added to the Manage view for each game, displaying its recommended configuration fetched directly from the OptiScaler compatibility page.
  - **Help Resources**: The Help view now includes a direct link to the OptiScaler compatibility list.
- **Upscaler Capabilities**
  - **FSR 4.1.1 (Non-RDNA4)**: Experimental support for FSR 4.1.1 on non-RDNA4 graphics cards has been included. *(Note: This has not been extensively tested; bug reports are appreciated).*

## Improvements

- **Steam Integration Enhancements**
  - **Steam File Parsing Refactor**: The parsing logic for Steam files has been refactored to use the robust `ValveKeyValue` library (by @brittcraft).
- **Version Management**
  - **Smart Version Sorting**: Lists for OptiScaler, OptiPatcher, and FSR INT8 now automatically sort to show the latest available version first. This behavior can be toggled via a new switch in the settings (enabled by default).
- **Visual & UI Refinements**
  - **Native vs. Added Upscalers Differentiation**: Native in-game upscalers and those included via OptiScaler are now visually differentiated. Added upscalers will appear without a background on game covers and will display a purple border in the Manage view.
- **Linux Specifics**
  - **Default Configuration Path**: The default configuration path for Linux users has been moved to `~/.config` or `~/.local` for properly saving settings and client assets.
- **Localization**
  - **Simplified Chinese Translation**: Corrected and updated the Simplified Chinese translation (by @juij-fun).

## Fixes

- **Installation & Updates**
  - **Settings Backup & Restore**: Fixed an issue where game-specific settings were lost when updating OptiScaler. Settings are now backed up and restored automatically during updates.
  - **Reinstallation Functionality**: Resolved a bug where reinstalling OptiScaler via the client caused it to lose functionality.
- **Linux & SteamOS Fixes**
  - **FSR4 INT8 Deployment Bug**: Fixed a bug on SteamOS where the client failed to copy the FSR4 INT8 bundle to the correct game folder, preventing the OptiScaler GUI and FSR4 options from loading (e.g., in *Achilles: Legends Untold*).

## New Contributors

- @jorikvanveen made their first contribution with a nix devShell and build derivation.
- @brittcraft made their first contribution by refactoring Steam file parsing using ValveKeyValue.
- @juij-fun made their first contribution by correcting and updating the Simplified Chinese translation.
- @misterj05 made their first contribution by adding Heroic Games Launcher support.

#### [1.0.5] ####

# What’s Changed
This update introduces **Linux support** (tested on Ubuntu, CachyOS, and Bazzite) featuring a dedicated GPU detection system and Steam runtime optimizations. A new **Custom Version Management** system has been implemented, allowing the use of personal OptiScaler, FakeNVAPI, and NukemFG builds. Additionally, **FSR 4 INT 8** is now automatically selected based on GPU architecture, and the update includes advanced library filters and an anti-cheat warning system.

# Changelog

## Added
* **Linux Platform Support**
    * Compatibility has been added for **Ubuntu, CachyOS, and Bazzite**.
    * A GPU detection base for Linux was implemented using `sysfs` and system tools (by @RafaelHGOliveira).
    * Game scanning logic was adapted for Steam and custom sources on Linux (by @RafaelHGOliveira).
    * Important exclusions regarding **Proton and the Steam runtime** were added (by @RafaelHGOliveira).
    * GPU family detection by ID was implemented to correctly display the model in the main window.
* **Custom Version Management**
    * Support for adding and managing personal or custom versions of OptiScaler, FakeNVAPI, and NukemFG was added.
    * Custom versions now appear under a dedicated **"Custom" tab** in the game management window.
    * Any version can now be set as the system default through the version management menu.
* **Advanced Scanning Filters**
    * New filters in the scanning window allow toggling between three modes:
        1. **Scan All**: Includes games where compatible upscaler DLLs are not detected.
        2. **Hide Non-Upscalers**: Scans all sources but hides games without detected upscalers.
        3. **Strict Scan**: Only shows games containing upscaler DLLs (experimental).
* **Folder Cleanup Tool**
    * A "Clean Folder" option was added to the game management window to remove corrupt files or residues from previous installations.
* **Safety & Information**
    * **Anti-cheat Warning**: Alerts were added for games where installing OptiScaler may cause issues (by @Louloubiwan).
    * **Welcome Screen**: A one-time introduction screen with platform-specific information was implemented.
    * **Help & FAQ**: Added instructions for Linux FSR 4 detection and folder recovery procedures.
* **Network Connectivity**
    * A **Proxy Configuration** option was added to the Settings tab.

## Improvements
* **Smart Architecture Logic**
    * **FSR 4 INT 8** is now selected by default when a non-RDNA 4 AMD GPU is detected.
* **UI/UX Enhancements**
    * The OptiScaler version selector is now divided into three tabs: **Stable, Beta, and Custom**.
    * The icon library was standardized for consistency between Linux and Windows.
    * DLL version mapping was implemented for DLSS, XeSS, and FSR to show traditional version numbers.
    * FakeNVAPI and NukemFG are now presented as selectors in the game management window for easier access.
    * Custom sources are now included in the scanning menu of the Games tab.
* **Process Optimization**
    * GitHub API request handling was improved to prevent recurrent **403 errors** and redundant calls.
    * Redundant version info was removed from the Help tab; management is now centralized in Settings.
    * Information was added clarifying that OptiScaler 0.9+ includes FakeNVAPI and NukemFG in the bundle.
    * The system now detects corrupt installations and suggests a cleanup before proceeding.

## Fixes
* **Linux Specifics**
    * A problem where disk paths in Linux appeared repeated or redundant was resolved.
    * An issue where icons appeared broken across the app on certain distributions was fixed.
* **Installation & Stability**
    * An error where performing an "Update" after an installation corrupted the folder was fixed.
    * A bug where choosing the Stable version as default still installed the Beta version was resolved.
    * An issue where the uninstaller deleted original game files shared with OptiScaler was corrected.
* **UI & Logic**
    * The cover art toast getting stuck when no SteamGrid key was set has been fixed.
    * A crash triggered when displaying games in List Mode was resolved.
    * A bug where opening the client for the first time showed no available versions was fixed.
    * The logic for FakeNVAPI/NukemFG options being active on versions 0.9+ was corrected.
* **General**
    * GitHub Issue templates were corrected and improved (by @PhrozenByte).
    * Localization fixes were applied for **Simplified Chinese** (by @juji-fun).

## New Contributors
* @RafaelHGOliveira made their first contribution regarding Linux support.
* @Louloubiwan made their first contribution with the Anti-cheat warning system.
* @PhrozenByte made their first contribution in GitHub templates.
* @juji-fun made their first contribution in Simplified Chinese localization.

#### [1.0.4] ####

# What’s Changed
This update introduces a major overhaul to your library management with a new **Grid View**, manual metadata editing, and a powerful **Profiles system** for `.ini` customization. We've added **SteamGridDB** support for better cover art fetching and a new **multi-threaded scanning** engine to keep the UI fluid. Additionally, users can now choose their default GPU, install **OptiPatcher** alongside OptiScaler, and manage global installation defaults through a unified settings menu.

# Changelog

## Added
* **Default GPU Selection**
    * You can now choose which GPU the application will use by default.
* **Grid View & Library Organization**
    * Added a toggle to switch between List and Grid views (Grid is now the default).
    * New **Organization Mode** allows you to reorder, hide, or show games in your list.
* **Manual Game Editing (by @luismaSE )**
    * Titles and cover art can now be manually edited from the Game Management window.
* **OptiPatcher Integration**
    * Added the option to install **OptiPatcher** together with OptiScaler from the management window.
* **SteamGridDB Support**
    * Optional API Key integration in settings to improve fetching for covers not found by primary methods.
* **Advanced Scanning Options**
    * New **Scanning Window** to select specific sources and drives before starting a scan.
    * Added a "Covers Only" scan mode to update missing art without rescanning all drives.
* **Profiles System**
    * New sidebar section to manage `.ini` configuration files applied during installation.
    * **Easy Mode:** Simple toggles for common settings like default upscaler, hotkeys, and overlays.
    * **Advanced Mode:** Provides full access to all OptiScaler configuration strings.
    * Supports creating, editing, deleting, setting defaults, and importing/exporting `.ini` files.
* **Help & Documentation**
    * Renamed "About" to **Help** and implemented a paginated info system including a detailed usage guide and common fixes.
* **Cache Management**
    * Added a button to clear cached application data to resolve potential errors.

## Improvements
* **Installation Feedback**
    * Clearer visual feedback when downloading and installing OptiScaler versions not currently in cache.
* **Clean Installation Logic**
    * Improved the install/uninstall algorithm to be more robust and prevent "dirty" folders.
* **Performance & Multi-threading**
    * The game scanning algorithm is now **multi-threaded**, significantly increasing speed.
    * Cover art fetching now happens in the background, keeping the app responsive during the process.
* **UI/UX Enhancements**
    * The Game Management window has been redesigned for better aesthetics and visibility (by @luismaSE ).
    * Added a disclaimer to the Help window.
* **Unified Default Settings**
    * Replaced the "Prefer Beta" toggle with a selector for Stable or Beta versions.
    * Created a unified menu to set default versions for **OptiScaler, FSR4 INT 8, and OptiPatcher** used in Quick, Bulk, and Manual installs.

## Fixes
* **UI Responsiveness**
    * Improved thread management for downloading, extracting, and installing to prevent UI freezes.
* **Download Handling**
    * Fixed errors when attempting multiple downloads of the same version simultaneously.
* **Version & Cache Errors**
    * Resolved the "Optiscaler.dll or nvngx.dll not found" error caused by corrupted cache files.
    * Fixed incorrect labeling and sorting where the latest versions appeared at the bottom of the list.
* **Quick Install Logic**
    * Fixed a bug where Quick Install would ignore the default FSR4 INT 8 configuration.
* **General Stability**
    * Fixed a crash triggered by scanning specific full directory paths.
    * Resolved an issue where changing the interface language caused several UI elements to disappear.

## What's Changed
* Feat/edit game info by @luismaSE in https://github.com/Agustinm28/Optiscaler-Client/pull/38
* PR for v1.0.4 by @Agustinm28 in https://github.com/Agustinm28/Optiscaler-Client/pull/47

## New Contributors
* @luismaSE made their first contribution in https://github.com/Agustinm28/Optiscaler-Client/pull/38

#### [1.0.3.1] ####

## Legal Clarity & License Update

This update focuses on improving the project's open-source compliance and legal structure. While these aren't "feature" changes, they are essential for the long-term health and professionalism of **Optiscaler-Client**.

### Why these changes?
To ensure the project is properly protected and easy for others to use or contribute to, I have explicitly asserted copyright ownership and clarified the licensing terms. This follows open-source best practices and provides clear guidance for downstream users and developers.

## Changelog

### Legal & Licensing
- **Explicit Copyright Notice:** Added `Copyright (c) 2026 ...` to the repository to clearly assert ownership.
- **License Clarification:** Formally defined the project as **GPL-3.0-or-later** to ensure compatibility with future license versions.
- **Source Headers:** Started adding license and copyright headers to individual source files to prevent them from becoming "orphaned" if shared separately.
- **Credits & About Section:** Added references to the respective licenses of **Optiscaler, Fakenvapi and NukemFG**, acknowledging the amazing tools that make this client possible.

---

### What's next?

I want to let you know that **Version 1.0.4** is currently in development and will bring more significant features and improvements. 

I’m working as fast as I can in my free time to get it ready. I truly appreciate your patience and the incredible feedback you’ve been sending—it helps a lot in making this tool better for everyone**

#### [1.0.3] ####

## What’s Changed

This update introduces support for OptiScaler beta versions with configurable defaults, automatic FSR 4 INT8 selection for non-RDNA 4 GPUs, and new Quick Install/Uninstall and Bulk Install features for faster setup across games. It also brings UI improvements like tooltips, more accurate cover detection, and updated badges, along with performance enhancements, faster downloads, and fixes for FSR detection issues.

## Changelog

### Added
- **Beta versions of OptiScaler**
  - You can now select beta versions of OptiScaler from the game manager.
  - A new setting allows you to choose whether **beta** or **latest** versions are selected by default during installation.  
    _This affects Manage, Quick Install, and Bulk Install._

- **Automatic FSR 4 INT8 injection for non-RDNA 4 GPUs**
  - Automatically detects if your GPU is not RDNA 4 and selects the latest available FSR 4 INT8 version by default.
  - You can configure which version should be selected by default for all installations.  
    _This affects Manage, Quick Install, and Bulk Install._
  - Downloads for these versions can be managed from the same menu used to handle the OptiScaler version cache in Settings.

- **Quick Install / Uninstall**
  - Added a button next to **Manage** for quick installation of OptiScaler using default settings.
  - Includes a quick uninstall button as well.

- **Bulk Install**
  - Added a new header menu that allows installing OptiScaler across multiple games at once.

### Improvements
- Added helpful tooltips to selectors in the game management window.
- Improved cover art fetching:
  - More accurate results.
  - Reduced duplicate covers.
- Updated **Latest** and **Beta** badges with improved visuals.
- Improved how OptiScaler versions are fetched and displayed to avoid conflicts with the GitHub API.
- The app now remembers:
  - Window resolution
  - Whether the window was maximized

### Fixes
- Fixed an issue where the system failed to detect the FSR DLL in certain games.
- Optimized download and extraction of newly installed versions.

#### [1.0.2] ####

## What's Changed

This release introduces a major migration from WPF to Avalonia UI, resulting in improved performance, reduced resource usage, and a lighter portable build, while laying the groundwork for future Linux support.
It also includes UI refinements, new configuration options (animations, auto-scan, and customizable scan sources), improved GPU VRAM detection, and expanded localization with multiple new languages.
Due to the scope of these framework changes, this version will remain in pre-release for a period of time—please try it out and report any bugs you encounter.

## Changelog

- Project migrated from WPF to Avalonia UI, which implies:
  - Improved overall performance with lower resource usage
  - Lighter builds with reduced portable size
  - A more solid foundation for future Linux support

- Visual interface enhancements:
  - Added subtle animations
  - Resized UI components
  - Reorganized some buttons and checkboxes for better layout

- New features:
  - Animations can now be enabled/disabled from settings
  - Auto-scan on startup can be enabled/disabled from settings
  - Scan sources can now be managed:
    - Default sources (Steam, EA, Epic, etc.) can be toggled
    - Custom sources (folder scanning) can be added via settings

- Fixes:
  - Improved GPU VRAM detection using Windows Registry as primary source (by @pablofleite in #9)
  - Fixed a bug where selecting languages ​​other than English made it impossible to configure custom scan fonts

## Translation related updates

- Added support for additional languages:
  - French
  - Italian
  - Russian
  - German
  - Polish
  - Dutch
  - Japanese
  - Traditional Chinese
  - Simplified Chinese
  - Korean

#### [1.0.1] ####

## What's Changed
This update improves overall clarity and usability. The project has been renamed to avoid confusion, installation issues with FakenvAPI have been fixed, and incorrect redirection to the GitHub repository has been resolved. Additionally, helpful descriptions have been added next to the FakenvAPI and NukemFG options to make them easier to understand.

## Changelog

- Renamed the project to avoid confusion.
- Fixed an issue in FakenvAPI that prevented installation due to a missing download requirement.
- Corrected the redirection to the GitHub repository.
- Added contextual help next to the FakenvAPI and NukemFG options, including descriptions of their functionality.

## Translation related updates

- Added Portuguese translation (by @pablofleite in #1)

## New Contributors

- Thanks to @pablofleite for his contribution in adding the Portuguese language.

#### [1.0.0] ####

OptiscalerClient first release.

#### [SUPPORT] ####

If you enjoy using OptiScaler Client and it makes things easier for you, you can now support its development! It is completely optional, but any support is deeply appreciated and helps maintain and improve the project.

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/agustinm28)