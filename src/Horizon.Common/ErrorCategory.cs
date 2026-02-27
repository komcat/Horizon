namespace Horizon.Common;

public enum ErrorCategory : byte
{
    General    = 0x00,
    Motion     = 0x01,
    IO         = 0x02,
    Pneumatic  = 0x03,
    Camera     = 0x04,
    Process    = 0x09,
    Safety     = 0x0F
}
