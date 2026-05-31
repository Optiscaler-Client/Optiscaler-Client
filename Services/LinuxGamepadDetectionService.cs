using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("linux")]
public class LinuxGamepadDetectionService : IGamepadDetectionService
{
    public event EventHandler<GamepadEventArgs>? GamepadInputReceived;
    public event EventHandler<bool>? GamepadConnectionChanged;
    
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isConnected = false;

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
        string devicePath = "/dev/input/js0"; // Simplest approach for first gamepad
        while (!token.IsCancellationRequested)
        {
            if (File.Exists(devicePath))
            {
                if (!_isConnected)
                {
                    _isConnected = true;
                    GamepadConnectionChanged?.Invoke(this, true);
                }

                try
                {
                    using var stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    byte[] buffer = new byte[8];
                    while (!token.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, 0, 8, token);
                        if (read == 8)
                        {
                            // evdev js struct: 
                            // uint32 time, int16 value, uint8 type, uint8 number
                            int value = BitConverter.ToInt16(buffer, 4);
                            byte type = buffer[6];
                            byte number = buffer[7];

                            const byte JS_EVENT_BUTTON = 0x01;
                            const byte JS_EVENT_AXIS = 0x02;

                            bool isInit = (type & 0x80) != 0;
                            if (isInit) continue;

                            type &= 0x7F;

                            if (type == JS_EVENT_BUTTON)
                            {
                                bool isPressed = value == 1;
                                GamepadButton btn = number switch
                                {
                                    0 => GamepadButton.A,
                                    1 => GamepadButton.B,
                                    2 => GamepadButton.X,
                                    3 => GamepadButton.Y,
                                    4 => GamepadButton.L1,
                                    5 => GamepadButton.R1,
                                    6 => GamepadButton.Back,
                                    7 => GamepadButton.Start,
                                    _ => GamepadButton.None
                                };

                                if (btn != GamepadButton.None)
                                    GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = btn, IsPressed = isPressed });
                            }
                            else if (type == JS_EVENT_AXIS)
                            {
                                // Left stick X (axis 0)
                                if (number == 0)
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftRight, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftLeft, IsPressed = true });
                                    else
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftRight, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftLeft, IsPressed = false });
                                    }
                                }
                                // Left stick Y (axis 1) — positive = down on Linux joystick driver
                                else if (number == 1)
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftDown, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftUp, IsPressed = true });
                                    else
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftDown, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbLeftUp, IsPressed = false });
                                    }
                                }
                                // Right stick X (axis 3)
                                else if (number == 3)
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightRight, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightLeft, IsPressed = true });
                                    else
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightRight, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightLeft, IsPressed = false });
                                    }
                                }
                                // Right stick Y (axis 4) — positive = down
                                else if (number == 4)
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightDown, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightUp, IsPressed = true });
                                    else
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightDown, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.ThumbRightUp, IsPressed = false });
                                    }
                                }
                                // D-Pad horizontal (axis 6) — Standard Xbox on Linux: 6 is horizontal, 7 is vertical
                                else if (number == 6) // Horizontal
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadRight, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadLeft, IsPressed = true });
                                    else 
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadRight, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadLeft, IsPressed = false });
                                    }
                                }
                                else if (number == 7) // Vertical
                                {
                                    if (value > 16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadDown, IsPressed = true });
                                    else if (value < -16000) GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadUp, IsPressed = true });
                                    else 
                                    {
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadDown, IsPressed = false });
                                        GamepadInputReceived?.Invoke(this, new GamepadEventArgs { Button = GamepadButton.DPadUp, IsPressed = false });
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        GamepadConnectionChanged?.Invoke(this, false);
                    }
                    // If device disconnected or error, wait and retry
                    await Task.Delay(2000, token);
                }
            }
            else
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    GamepadConnectionChanged?.Invoke(this, false);
                }
                await Task.Delay(2000, token);
            }
        }
    }
}
