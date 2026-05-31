using System;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public interface IGamepadDetectionService
{
    event EventHandler<GamepadEventArgs>? GamepadInputReceived;
    event EventHandler<bool>? GamepadConnectionChanged;
    void StartListening();
    void StopListening();
}
