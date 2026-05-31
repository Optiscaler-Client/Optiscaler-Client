namespace OptiscalerClient.Models;

public enum GamepadButton
{
    None = 0,
    DPadUp = 1,
    DPadDown = 2,
    DPadLeft = 3,
    DPadRight = 4,
    A = 5,
    B = 6,
    X = 7,
    Y = 8,
    Start = 9,
    Back = 10,
    L1 = 11,
    R1 = 12,
    // Thumbstick directions (generated when the axis crosses the deadzone threshold)
    ThumbLeftUp = 13,
    ThumbLeftDown = 14,
    ThumbLeftLeft = 15,
    ThumbLeftRight = 16,
    ThumbRightUp = 17,
    ThumbRightDown = 18,
    ThumbRightLeft = 19,
    ThumbRightRight = 20
}
