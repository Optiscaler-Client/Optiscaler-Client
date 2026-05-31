using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]
public class WindowsGamepadDetectionService : IGamepadDetectionService
{
    public event EventHandler<GamepadEventArgs>? GamepadInputReceived;
    public event EventHandler<bool>? GamepadConnectionChanged;
    
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private ushort _lastButtons = 0;
    private bool _isConnected = false;

    // Thumbstick state tracking — one bool per direction, set when axis crosses deadzone
    private const short ThumbDeadzone = 10000;
    private bool _tlUp, _tlDown, _tlLeft, _tlRight;   // left stick
    private bool _trUp, _trDown, _trLeft, _trRight;   // right stick

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetStateFallback(int dwUserIndex, out XINPUT_STATE pState);

    private bool _useFallback = false;

    public void StartListening()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollLoop(_cts.Token), _cts.Token);
    }

    public void StopListening()
    {
        _cts?.Cancel();
        try { _pollingTask?.Wait(500); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            XINPUT_STATE state;
            int result = 1;
            
            try 
            {
                if (!_useFallback)
                    result = XInputGetState(0, out state);
                else
                    result = XInputGetStateFallback(0, out state);
            }
            catch (DllNotFoundException)
            {
                if (!_useFallback)
                {
                    _useFallback = true;
                    continue;
                }
                // Si no hay XInput, cancelamos silenciosamente
                return;
            }

            if (result == 0) // ERROR_SUCCESS
            {
                if (!_isConnected)
                {
                    _isConnected = true;
                    GamepadConnectionChanged?.Invoke(this, true);
                }

                ushort currentButtons = state.Gamepad.wButtons;
                
                // Compare with last state
                if (currentButtons != _lastButtons)
                {
                    CheckButton(currentButtons, _lastButtons, 0x0001, GamepadButton.DPadUp);
                    CheckButton(currentButtons, _lastButtons, 0x0002, GamepadButton.DPadDown);
                    CheckButton(currentButtons, _lastButtons, 0x0004, GamepadButton.DPadLeft);
                    CheckButton(currentButtons, _lastButtons, 0x0008, GamepadButton.DPadRight);
                    CheckButton(currentButtons, _lastButtons, 0x0010, GamepadButton.Start);
                    CheckButton(currentButtons, _lastButtons, 0x0020, GamepadButton.Back);
                    CheckButton(currentButtons, _lastButtons, 0x0100, GamepadButton.L1);
                    CheckButton(currentButtons, _lastButtons, 0x0200, GamepadButton.R1);
                    CheckButton(currentButtons, _lastButtons, 0x1000, GamepadButton.A);
                    CheckButton(currentButtons, _lastButtons, 0x2000, GamepadButton.B);
                    CheckButton(currentButtons, _lastButtons, 0x4000, GamepadButton.X);
                    CheckButton(currentButtons, _lastButtons, 0x8000, GamepadButton.Y);
                    
                    _lastButtons = currentButtons;
                }

                // Thumbstick axes are always checked (deadzone filtering inside)
                CheckThumbSticks(state.Gamepad);
            }
            else
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    GamepadConnectionChanged?.Invoke(this, false);
                }
            }
            
            await Task.Delay(16, token); // ~60fps
        }
    }

    private void CheckButton(ushort current, ushort last, ushort mask, GamepadButton button)
    {
        bool isCurrentlyPressed = (current & mask) == mask;
        bool wasPressed = (last & mask) == mask;

        if (isCurrentlyPressed && !wasPressed)
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = button, IsPressed = true });
        else if (!isCurrentlyPressed && wasPressed)
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = button, IsPressed = false });
    }

    private void CheckThumbSticks(XINPUT_GAMEPAD gamepad)
    {
        // Left stick
        CheckThumbAxis(gamepad.sThumbLX,  ref _tlRight, ref _tlLeft,  GamepadButton.ThumbLeftRight,  GamepadButton.ThumbLeftLeft);
        CheckThumbAxis(gamepad.sThumbLY,  ref _tlUp,    ref _tlDown,  GamepadButton.ThumbLeftUp,     GamepadButton.ThumbLeftDown);
        // Right stick
        CheckThumbAxis(gamepad.sThumbRX,  ref _trRight, ref _trLeft,  GamepadButton.ThumbRightRight, GamepadButton.ThumbRightLeft);
        CheckThumbAxis(gamepad.sThumbRY,  ref _trUp,    ref _trDown,  GamepadButton.ThumbRightUp,    GamepadButton.ThumbRightDown);
    }

    // Fires pressed/released events when an axis crosses (+/-) the deadzone.
    // XInput Y axes: positive = up, negative = down.
    private void CheckThumbAxis(short value,
        ref bool positiveActive, ref bool negativeActive,
        GamepadButton positiveBtn, GamepadButton negativeBtn)
    {
        bool pos = value >  ThumbDeadzone;
        bool neg = value < -ThumbDeadzone;

        if (pos && !positiveActive)
        {
            positiveActive = true;
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = positiveBtn, IsPressed = true });
        }
        else if (!pos && positiveActive)
        {
            positiveActive = false;
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = positiveBtn, IsPressed = false });
        }

        if (neg && !negativeActive)
        {
            negativeActive = true;
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = negativeBtn, IsPressed = true });
        }
        else if (!neg && negativeActive)
        {
            negativeActive = false;
            GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = negativeBtn, IsPressed = false });
        }
    }
}
