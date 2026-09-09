using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views;

/// <summary>
/// The manual/visible half of "Setup NR" — waiting for the user to run danielblnc's installer
/// (already downloaded and staged into the game folder by the caller before this window opens, see
/// ManageGameWindow.DownloadAndStageDanielModAsync) through its console prompts by hand. This is the
/// "Manual Install" path; "Auto Install" drives the same installer headlessly instead (see
/// DlssNrOnAmdService.RunAutomatedInstallAsync) and never opens this window at all. Triggered from
/// ManageGameWindow's Manual Install button once a Setup NR mode is pending (see
/// Game.PendingDlssNrOnAmdMode — set by the inline CmbSetupNr selector). Install-only: uninstalling a
/// daniel-only mod never reopens this window or reruns the installer — it just replays the manifest
/// saved here in reverse (see DlssNrOnAmdService.RestoreFromManifest, called directly from
/// ManageGameWindow).
///
/// Also doubles as the standalone nvngx_dlssnr.dll picker (constructor's <c>nvngxPickerOnly</c>) used
/// by ManageGameWindow's Auto Install path to get that one-time file without going through the rest of
/// this window — see the constructor and BtnPickNvngx_Click for how that mode short-circuits.
/// </summary>
public partial class DlssNrOnAmdWizardWindow : Window
{
    private readonly Game _game = null!;
    private readonly DlssNrOnAmdService _dlssNrService = new();
    private string _danielVersion = "";
    private bool _isModeB;
    private bool _nvngxPickerOnly;

    private DlssNrOnAmdService.InstallSession? _session;

    // Polls for the weights marker instead of requiring an explicit "I'm done" click. Works
    // regardless of how the installer was launched — directly by us (Game.DlssNrDefenderExclusionAdded)
    // or by the user double-clicking it themselves from the opened folder — since either way we can't
    // read the installer's own console, only the filesystem effect it leaves behind once done. Not
    // started at all in nvngxPickerOnly mode (there's no installer running yet at that point).
    private DispatcherTimer? _weightsPollTimer;

    /// <summary>True once the wizard actually completed its job — installed danielblnc's mod (normal
    /// mode), or successfully imported nvngx_dlssnr.dll (nvngxPickerOnly mode) — so the caller knows
    /// whether to proceed or stop.</summary>
    public bool Succeeded { get; private set; }

    public DlssNrOnAmdWizardWindow() => InitializeComponent();

    /// <param name="nvngxPickerOnly">When true, this window only asks for nvngx_dlssnr.dll (the
    /// one-time, machine-wide cache — see DlssNrOnAmdService) and closes as soon as that's picked,
    /// skipping the instructions/installer-launch panel and the weights poll entirely. Used by
    /// ManageGameWindow's Auto Install to get that file without showing the rest of this window; the
    /// caller is expected to only construct it this way when DlssNrOnAmdService.IsNvngxDlssNrCached()
    /// is already false.</param>
    public DlssNrOnAmdWizardWindow(Window owner, Game game, string danielVersion, bool isModeB, bool nvngxPickerOnly = false) : this()
    {
        Owner = owner;
        _game = game;
        _danielVersion = danielVersion;
        _isModeB = isModeB;
        _nvngxPickerOnly = nvngxPickerOnly;

        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
            titleBar.PointerPressed += (_, e) => BeginMoveDrag(e);

        if (_nvngxPickerOnly)
        {
            this.FindControl<StackPanel>("PanelNvngxPicker")!.IsVisible = true;
            this.FindControl<StackPanel>("PanelWizard")!.IsVisible = false;
            return;
        }

        // Moved here from the now-removed SetupDlssNrWindow modal — gate the instructions step on it
        // since Stage() (already run by the caller before this window opened) silently skips copying
        // nvngx_dlssnr.dll when it isn't cached yet, which would let danielblnc's installer run
        // without it (no real weights generated). ManageGameWindow's Auto Install avoids ever hitting
        // this by getting it via the nvngxPickerOnly mode above before staging at all.
        var nvngxCached = _dlssNrService.IsNvngxDlssNrCached();
        this.FindControl<StackPanel>("PanelNvngxPicker")!.IsVisible = !nvngxCached;
        this.FindControl<StackPanel>("PanelWizard")!.IsVisible = nvngxCached;

        this.FindControl<TextBlock>("TxtWizardInstructions")!.Text = isModeB
            ? GetResourceString("TxtSetupNrInstructionsModeB",
                "danielblnc's mod was downloaded successfully, but it needs a manual install:\n" +
                "1) Click \"Run installer\".\n" +
                "2) Follow the steps shown in the terminal. IMPORTANT: when it asks which proxy DLL to use, pick one DIFFERENT from dxgi.dll (e.g. dbghelp.dll) — OptiScaler will be installed as dxgi.dll afterwards and they need separate slots.\n\n" +
                "This window closes automatically and installs the OptiScaler wrapper once it detects the installer finished.")
            : GetResourceString("TxtSetupNrInstructionsModeA",
                "danielblnc's mod was downloaded successfully, but it needs a manual install:\n" +
                "1) Click \"Run installer\".\n" +
                "2) Follow the steps shown in the terminal (any proxy DLL it offers works).\n\n" +
                "This window closes automatically once it detects the installer finished.");

        // Baseline for the manifest — taken now (after Stage()'s own copy already ran) so it also
        // covers whatever danielblnc's installer changes once the user runs it.
        var gameDir = ResolveGameDir();
        if (gameDir != null) _session = _dlssNrService.BeginInstallSession(gameDir);

        // No "I'm done" button — poll for the weights marker instead (see field doc). 1s is frequent
        // enough to feel instant without meaningfully polling the disk.
        _weightsPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _weightsPollTimer.Tick += (_, _) =>
        {
            if (TryDetectDanielInstall())
            {
                _weightsPollTimer.Stop();
                Close(true);
            }
        };
        _weightsPollTimer.Start();
        this.Closed += (_, _) => _weightsPollTimer?.Stop();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Same folder-resolution OptiScaler's own install uses (GameInstallationService.
    /// DetermineInstallDirectory — handles Unreal's Binaries/Win64 and Phoenix layouts, not just
    /// InstallPath's root), kept in sync with ManageGameWindow.ResolveDanielModGameDir.</summary>
    private string? ResolveGameDir() => new GameInstallationService().DetermineInstallDirectory(_game);

    /// <summary>Plain visible launch (or opens the folder with the exe pre-selected, if the game
    /// folder's Defender exclusion isn't confirmed yet) — a real console window, the user answers its
    /// prompts by hand. This is the whole of "Manual Install"; unlike Auto Install, nothing here is
    /// automated. Stage()'s own notes record a past case where Process.Start-ing this exact kind of
    /// unsigned exe from inside this app's own process got it quarantined outright with no recovery,
    /// unlike a normal Explorer double-click — so falling back to opening the folder whenever the
    /// exclusion isn't confirmed keeps the safer path as the default.</summary>
    private void BtnOpenGameFolder_Click(object? sender, RoutedEventArgs e)
    {
        var gameDir = ResolveGameDir();
        if (gameDir == null) return;
        var stagedExe = Path.Combine(gameDir, DlssNrOnAmdService.StagedExeFileName);

        if (_game.DlssNrDefenderExclusionAdded)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = stagedExe, UseShellExecute = true, WorkingDirectory = gameDir });
                return;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not launch installer directly, falling back to opening the folder: {ex.Message}");
            }
        }

        PlatformServiceFactory.CreateShellService().OpenFolderAndSelect(stagedExe);
    }

    private async void BtnPickNvngx_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = GetResourceString("TxtSetupNrPickNvngxTitle", "Select nvngx_dlssnr.dll"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("nvngx_dlssnr.dll") { Patterns = new[] { "nvngx_dlssnr.dll", "*.dll" } } }
        });
        if (files.Count == 0) return;

        var path = files[0].Path.IsAbsoluteUri ? files[0].Path.LocalPath : files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            _dlssNrService.ImportNvngxDlssNr(path);

            if (_nvngxPickerOnly)
            {
                Succeeded = true;
                Close(true);
                return;
            }

            // The exe was already staged before this window opened, without nvngx (it wasn't cached
            // yet) — re-stage now that it is, so it actually lands in the game folder this run.
            var gameDir = ResolveGameDir();
            if (gameDir != null) _dlssNrService.Stage(_danielVersion, gameDir);

            this.FindControl<StackPanel>("PanelNvngxPicker")!.IsVisible = false;
            this.FindControl<StackPanel>("PanelWizard")!.IsVisible = true;
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError(string.Format(GetResourceString("TxtSetupNrCacheNvngxError", "Could not cache the file: {0}"), ex.Message));
        }
    }

    /// <summary>Checks for the weights marker and, if present, records the mod as installed —
    /// polled by _weightsPollTimer, and also run once more from the titlebar X close in case the
    /// installer finished in the split second before the window closed. Silent (no error shown) since
    /// "not found yet" is the expected, normal state while the user is still working through the
    /// installer's own prompts, not a failure.</summary>
    private bool TryDetectDanielInstall()
    {
        if (_session == null) return false;
        if (!_dlssNrService.TryFinishInstall(_session, _game, _danielVersion, _isModeB)) return false;
        Succeeded = true;
        return true;
    }

    private void ShowError(string? message)
    {
        var txt = this.FindControl<TextBlock>("TxtError")!;
        txt.IsVisible = !string.IsNullOrEmpty(message);
        txt.Text = message ?? "";
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (_nvngxPickerOnly) { Close(false); return; }
        if (!Succeeded) TryDetectDanielInstall();
        Close(Succeeded);
    }

    private string GetResourceString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
    }
}
