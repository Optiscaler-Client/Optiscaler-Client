using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Shared plumbing for the gamepad navigation helpers (main window, Bulk
/// Install, simple dialogs). Extracted from three near-identical copies of
/// the same key-simulation / viewport-visibility logic — see
/// context/plans/gamepad_refactor_plan.md, Fase 1.
/// </summary>
public abstract class GamepadHelperBase
{
    protected readonly Window _window;

    protected GamepadHelperBase(Window window)
    {
        _window = window;
        _window.PointerMoved += OnWindowPointerMovedForModeDetection;
    }

    private bool _isGamepadModeActive;
    private Point? _lastPointerPositionForModeDetection;

    /// <summary>
    /// True once gamepad input has been used more recently than real mouse
    /// movement in this window's lifetime. Drives cosmetic swaps like
    /// showing a "B" badge instead of a mouse-oriented "X" close icon on a
    /// window's close button — see gamepad_implementation_log.md, section 24.
    /// </summary>
    public bool IsGamepadModeActive => _isGamepadModeActive;

    /// <summary>Raised whenever <see cref="IsGamepadModeActive"/> changes.</summary>
    public event EventHandler<bool>? GamepadModeActiveChanged;

    /// <summary>Called by derived classes whenever real gamepad input (button or stick) arrives.</summary>
    protected void MarkGamepadModeActive() => SetGamepadModeActive(true);

    private void OnWindowPointerMovedForModeDetection(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(_window);

        if (!_isGamepadModeActive)
        {
            _lastPointerPositionForModeDetection = position;
            return;
        }

        // Avalonia re-hit-tests under a stationary cursor whenever a Popup
        // appears (e.g. opening a ComboBox dropdown with the gamepad), which
        // raises a synthetic PointerMoved with no real mouse motion. Only
        // genuine movement should revert to mouse mode — same fix as
        // MainWindow_PointerMoved, see gamepad_implementation_log.md, section 15.
        if (_lastPointerPositionForModeDetection is { } last)
        {
            var delta = position - last;
            if (Math.Abs(delta.X) < 1.0 && Math.Abs(delta.Y) < 1.0)
            {
                _lastPointerPositionForModeDetection = position;
                return;
            }
        }

        _lastPointerPositionForModeDetection = position;
        SetGamepadModeActive(false);
    }

    private void SetGamepadModeActive(bool active)
    {
        if (_isGamepadModeActive == active) return;
        _isGamepadModeActive = active;
        GamepadModeActiveChanged?.Invoke(this, active);
    }

    /// <summary>
    /// Forces the initial value of <see cref="IsGamepadModeActive"/> instead
    /// of waiting for the first PointerMoved/gamepad event on this window.
    /// A newly opened dialog should already show the "B" badge if its owner
    /// was already in gamepad mode, rather than only picking that up once
    /// the user presses something inside the new window — see
    /// gamepad_implementation_log.md, section 25. Always raises
    /// <see cref="GamepadModeActiveChanged"/> (even if the value happens to
    /// match the default) so a listener subscribed right before calling this
    /// is guaranteed to receive the correct starting state.
    /// </summary>
    public void SeedGamepadModeActive(bool active)
    {
        _isGamepadModeActive = active;
        GamepadModeActiveChanged?.Invoke(this, active);
    }

    /// <summary>Must be called from each derived class's Dispose() to avoid leaking the PointerMoved subscription.</summary>
    protected void UnhookGamepadModeDetection() => _window.PointerMoved -= OnWindowPointerMovedForModeDetection;

    /// <summary>
    /// Simulates a full key press (KeyDown + KeyUp) on the currently focused
    /// element, or on <paramref name="explicitTarget"/> if provided. Used to
    /// drive Avalonia's native focus/keyboard handling (Tab navigation,
    /// Enter/Space activation) from gamepad input instead of reimplementing
    /// it manually.
    /// </summary>
    protected void SimulateKey(Key key, KeyModifiers modifiers = KeyModifiers.None, Interactive? explicitTarget = null)
    {
        var target = explicitTarget ?? TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement() as Interactive;
        target ??= _window;

        var keyDownArgs = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = target
        };
        target.RaiseEvent(keyDownArgs);

        var keyUpArgs = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = target
        };
        target.RaiseEvent(keyUpArgs);
    }

    /// <summary>
    /// Whether <paramref name="control"/> intersects the visible viewport of
    /// <paramref name="scrollViewer"/>. Used to avoid focusing/selecting list
    /// items that are scrolled out of view (snapping to index 0 instead of
    /// the nearest visible row).
    /// </summary>
    protected static bool IsControlVisibleInScrollViewer(Visual? control, ScrollViewer? scrollViewer)
    {
        if (control == null || scrollViewer == null) return false;

        var transform = control.TransformToVisual(scrollViewer);
        if (transform == null) return false;

        var bounds = new Rect(default, control.Bounds.Size);
        var transformedBounds = bounds.TransformToAABB(transform.Value);

        var viewportBounds = new Rect(default, scrollViewer.Viewport);
        return viewportBounds.Intersects(transformedBounds);
    }

    /// <summary>
    /// Focuses <paramref name="element"/> using directional navigation so
    /// Avalonia's focus ring (ShowKeyboardCues-dependent styles) renders —
    /// plain <c>.Focus()</c> does not reliably trigger it. See
    /// gamepad_implementation_log.md, sections 5/13/14.
    /// </summary>
    protected static void FocusWithRing(IInputElement? element)
    {
        element?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Defers <paramref name="action"/> to the next UI dispatcher pass.
    /// Needed when reassigning focus right after closing a popup/ComboBox,
    /// since Avalonia's own layout pass on close overwrites focus set
    /// synchronously. See gamepad_implementation_log.md, section 14.
    /// </summary>
    protected static void DeferFocus(Action action, DispatcherPriority priority)
    {
        Dispatcher.UIThread.Post(action, priority);
    }

    /// <summary>
    /// True when this window currently owns another open window (e.g. a
    /// <c>ConfirmDialog</c> shown via <c>ShowDialog(this)</c>). Gamepad input
    /// must be ignored while that's true: the owned window has its own
    /// gamepad helper listening to the same physical device, and without this
    /// guard both helpers process the same button press — e.g. 'B' closing
    /// the confirmation dialog AND the window underneath it, since both
    /// report <c>IsVisible</c>/<c>true</c> while the modal child is open.
    /// See gamepad_implementation_log.md, section 16.
    ///
    /// This check alone is racy: each window's <c>IGamepadDetectionService</c>
    /// polls independently, so the owner's queued input-handling callback can
    /// run either before or after the child's — if the child's callback
    /// happens to close it first, <c>OwnedWindows</c> is already empty by the
    /// time the owner's callback checks it. Prefer <see cref="IsInputSuspended"/>
    /// (set deterministically by the child dialog itself) wherever the child
    /// implements <see cref="IGamepadInputHost"/>; keep this as a fallback for
    /// cases that don't. See gamepad_implementation_log.md, section 18.
    /// </summary>
    protected bool HasOpenOwnedWindow() => _window.OwnedWindows.Count > 0;

    private bool _isInputSuspended;

    /// <summary>True while a child dialog has explicitly suspended this helper (see section 18).</summary>
    protected bool IsInputSuspended => _isInputSuspended;

    /// <summary>
    /// Stops this helper from reacting to gamepad input until <see cref="ResumeInput"/>
    /// is called. Meant to be invoked by a child dialog (via <see cref="IGamepadInputHost"/>)
    /// for the exact lifetime of its own modal window, deterministically —
    /// unlike <see cref="HasOpenOwnedWindow"/>, this isn't derived reactively
    /// from window state that two racing polling loops can observe out of order.
    /// </summary>
    public void SuspendInput() => _isInputSuspended = true;

    /// <summary>Resumes input handling after <see cref="SuspendInput"/>.</summary>
    public void ResumeInput() => _isInputSuspended = false;
}

/// <summary>
/// Implemented by windows that own a <see cref="GamepadHelperBase"/>-derived
/// helper and can be the <c>owner</c> of a modal dialog (e.g. <c>ConfirmDialog</c>).
/// Lets the dialog suspend the owner's gamepad input for its own lifetime,
/// deterministically, instead of relying on a reactive/racy state check.
/// </summary>
public interface IGamepadInputHost
{
    GamepadHelperBase? GamepadHelper { get; }

    /// <summary>
    /// Whether this window is currently in "gamepad mode" (vs mouse mode).
    /// Used to seed a newly opened child dialog's own X→B close-badge state
    /// from its owner instead of always starting in mouse mode — see
    /// gamepad_implementation_log.md, section 26. Default implementation
    /// covers every window built on <see cref="GamepadHelperBase"/>; windows
    /// with a bespoke gamepad implementation instead (e.g. <c>ManageGameWindow</c>)
    /// override this directly, since they have no <see cref="GamepadHelper"/>.
    /// </summary>
    bool IsGamepadModeActive => GamepadHelper?.IsGamepadModeActive ?? false;
}
