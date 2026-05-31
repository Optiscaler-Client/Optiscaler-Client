using System;

namespace OptiscalerClient.Models;

public class GamepadEventArgs : EventArgs
{
    public GamepadButton Button { get; set; }
    public bool IsPressed { get; set; }
}
