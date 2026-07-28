using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OptiscalerClient.Models;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace OptiscalerClient.Views
{
    public partial class ConfirmDialog : Window
    {
        private GamepadDialogNavigationHelper? _gamepadHelper;

        public ConfirmDialog()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
        }

        public ConfirmDialog(Window? owner, string title, string message, bool isAlert = false, string? iconOverride = null)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            
            // 100% Flicker-free startup strategy:
            // 1. Invisible and at targeted position before becoming visible.
            // 2. No SystemDecorations to avoid OS frame jumps.
            this.Opacity = 0;
            this.Position = owner?.Position ?? new PixelPoint(0, 0); // Temporary but doesn't matter yet

            if (owner != null)
            {
                var scaling = owner.DesktopScaling;
                double dialogW = 440 * scaling;
                double dialogH = 220 * scaling;

                var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
                var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;
                
                this.Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));
            }

            var txtTitle = this.FindControl<TextBlock>("TxtTitle");
            var txtMessage = this.FindControl<TextBlock>("TxtMessage");
            var btnCancel = this.FindControl<Button>("BtnCancel");
            var btnConfirm = this.FindControl<Button>("BtnConfirm");
            var txtIcon = this.FindControl<TextBlock>("TxtIcon");
            var titleBar = this.FindControl<Border>("TitleBar");

            if (txtTitle != null) txtTitle.Text = title;
            if (txtMessage != null) txtMessage.Text = message;

            // Manual Dragging implementation for BorderOnly windows
            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) => 
                {
                    this.BeginMoveDrag(e);
                };
            }

            var defaultIcon = isAlert ? "\uE783" : "\uF63E";
            var defaultBrush = isAlert
                ? (Application.Current?.FindResource("BrAccentWarm") as IBrush ?? Brushes.Orange)
                : (Application.Current?.FindResource("BrAccentPrimary") as IBrush ?? Brushes.DeepSkyBlue);

            if (isAlert)
            {
                if (btnCancel != null) btnCancel.IsVisible = false;

                if (btnConfirm != null) 
                {
                    btnConfirm.Content = Application.Current?.TryFindResource("TxtGotIt", out var res) == true ? res?.ToString() ?? "Got it" : "Got it";
                }
            }

            if (txtIcon != null)
            {
                txtIcon.Text = iconOverride ?? defaultIcon;
                txtIcon.Foreground = iconOverride != null
                    ? (Application.Current?.FindResource("BrAccentPrimary") as IBrush ?? Brushes.DeepSkyBlue)
                    : defaultBrush;
            }

            this.Opened += (s, e) =>
            {
                // Force OS-level activation for this borderless (SystemDecorations="None")
                // window. Without it, the very first click after the dialog appears can be
                // consumed by Windows just to activate the window instead of reaching the
                // button underneath the cursor — most noticeable on BtnConfirm, since the
                // dialog opens centered over the owner where the mouse already sits after
                // clicking the triggering action.
                this.Activate();

                this.Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }
                if (_gamepadHelper == null)
                {
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, null);
                    _gamepadHelper.GamepadModeActiveChanged += OnGamepadModeActiveChanged;

                    // Start already in whatever mode the owner window was in,
                    // instead of always defaulting to mouse mode until the
                    // user presses something inside this new dialog — see
                    // gamepad_implementation_log.md, section 25.
                    if (owner is IGamepadInputHost seedHost)
                        _gamepadHelper.SeedGamepadModeActive(seedHost.IsGamepadModeActive);
                }

                // Give an explicit starting focus so the first D-Pad/A press
                // has a predictable target instead of relying on whatever
                // (if anything) Avalonia auto-focused on open.
                (btnConfirm ?? btnCancel)?.Focus(NavigationMethod.Directional);

                // Deterministically stop the owner window's own gamepad helper
                // from reacting while this dialog is open. Two independent
                // polling loops (owner's + this dialog's) both see the same
                // physical button press; without this, whichever one's queued
                // callback happens to run last after the dialog closes itself
                // can still process that same press (e.g. 'B' closing the
                // owner too). See gamepad_implementation_log.md, section 18.
                if (owner is IGamepadInputHost host)
                    host.GamepadHelper?.SuspendInput();
            };

            this.Closed += (s, e) =>
            {
                if (owner is IGamepadInputHost closedHost)
                    closedHost.GamepadHelper?.ResumeInput();

                if (_gamepadHelper != null)
                    _gamepadHelper.GamepadModeActiveChanged -= OnGamepadModeActiveChanged;
                _gamepadHelper?.Dispose();
                _gamepadHelper = null;
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnGamepadModeActiveChanged(object? sender, bool isGamepadModeActive)
        {
            var txtX = this.FindControl<TextBlock>("TxtCloseIconX");
            var badgeB = this.FindControl<Border>("BadgeCloseGamepadB");
            if (txtX != null) txtX.IsVisible = !isGamepadModeActive;
            if (badgeB != null) badgeB.IsVisible = isGamepadModeActive;
        }

        private bool _isAnimatingClose = false;

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated(false);
        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated(true);

        private async Task CloseAnimated(bool result)
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;
            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;
            await Task.Delay(220);
            Close(result);
        }
    }
}
