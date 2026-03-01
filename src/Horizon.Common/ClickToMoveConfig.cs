namespace Horizon.Common;

public sealed class ClickToMoveConfig
{
    public bool Enabled { get; set; } = true;
    public string MotionDevice { get; set; } = "gantry-main";
    public double PixelToMmX { get; set; } = 0.01;
    public double PixelToMmY { get; set; } = 0.01;
    public int CalibrationRefWidth { get; set; } = 1920;
    public int CalibrationRefHeight { get; set; } = 1200;
    public double Magnification { get; set; } = 1.0;
    public double MoveSpeedMmPerSec { get; set; } = 10.0;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
}
