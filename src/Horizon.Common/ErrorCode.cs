namespace Horizon.Common;

public enum ErrorCode : ushort
{
    // General (0x00xx)
    Success                     = 0x0000,
    UnknownError                = 0x0001,
    InvalidParameter            = 0x0002,
    Timeout                     = 0x0003,
    NotInitialized              = 0x0004,
    AlreadyInitialized          = 0x0005,
    OperationFailed             = 0x0006,
    InvalidConfiguration        = 0x0007,
    FileNotFound                = 0x0008,
    PermissionDenied            = 0x0009,
    NotSupported                = 0x000A,

    // Motion (0x01xx)
    MotionDeviceNotFound        = 0x0100,
    MotionAxisError             = 0x0101,
    MotionHomingFailed          = 0x0102,
    MotionLimitReached          = 0x0103,
    MotionPositionNotFound      = 0x0104,
    MotionCommunicationError    = 0x0105,
    MotionTimeout               = 0x0106,
    MotionNotConnected          = 0x0107,
    MotionAlreadyMoving         = 0x0108,
    MotionStopFailed            = 0x0109,

    // IO (0x02xx)
    IODeviceNotFound            = 0x0200,
    IODisconnected              = 0x0201,
    IOInvalidPin                = 0x0202,
    IOOperationFailed           = 0x0203,
    IONotInitialized            = 0x0204,
    IOConnectionFailed          = 0x0205,
    IOPinNotFound               = 0x0206,

    // Pneumatic (0x03xx)
    PneumaticSlideNotFound      = 0x0300,
    PneumaticIOManagerNull      = 0x0301,
    PneumaticDeviceNotFound     = 0x0302,
    PneumaticPinNotFound        = 0x0303,
    PneumaticTimeout            = 0x0304,
    PneumaticAlreadyAtPosition  = 0x0305,
    PneumaticHardwareError      = 0x0306,
    PneumaticOperationFailed    = 0x0307,

    // Camera (0x04xx)
    CameraNotFound              = 0x0400,
    CameraConnectionFailed      = 0x0401,
    CameraGrabFailed            = 0x0402,
    CameraCaptureFailed         = 0x0403,
    CameraAlreadyConnected      = 0x0404,
    CameraNotConnected          = 0x0405,

    // Current Measurement (0x05xx)
    CMeasDeviceNotFound         = 0x0500,
    CMeasDisconnected           = 0x0501,
    CMeasInvalidChannel         = 0x0502,
    CMeasOperationFailed        = 0x0503,
    CMeasCommunicationError     = 0x0504,
    CMeasParseError             = 0x0505,

    // Laser Controller (0x06xx)
    LaserDeviceNotFound         = 0x0600,
    LaserDisconnected           = 0x0601,
    LaserOperationFailed        = 0x0602,
    LaserCommunicationError     = 0x0603,
    LaserParseError             = 0x0604,

    // Process / Sequence (0x09xx)
    ProcessInvalidStep          = 0x0900,
    ProcessDeviceNotRegistered  = 0x0901,
    ProcessHandlerNotFound      = 0x0902,
    ProcessCommandFailed        = 0x0903,
    ProcessCancelled            = 0x0904,
    ProcessTimeout              = 0x0905,
    ProcessInvalidCondition     = 0x0906,
    ProcessAlreadyRunning       = 0x0907,

    // Safety (0x0Fxx)
    SafetyInputTriggered        = 0x0F00,
    SafetyCriticalFault         = 0x0F01,
    SafetyWatchStartFailed      = 0x0F02,
    SafetyAcknowledgeRequired   = 0x0F03,
    SafetyEnforcementExecuted   = 0x0F04,
    SafetyExecutionBlocked      = 0x0F05,

    // Interlock (0x0F1x)
    InterlockMotionBlocked      = 0x0F10,
    InterlockOverrideActive     = 0x0F11,
}

public static class ErrorCodeExtensions
{
    public static ErrorCategory GetCategory(this ErrorCode code)
    {
        var category = (byte)((ushort)code >> 8);
        return category switch
        {
            0x00 => ErrorCategory.General,
            0x01 => ErrorCategory.Motion,
            0x02 => ErrorCategory.IO,
            0x03 => ErrorCategory.Pneumatic,
            0x04 => ErrorCategory.Camera,
            0x05 => ErrorCategory.CurrentMeasurement,
            0x06 => ErrorCategory.LaserController,
            0x09 => ErrorCategory.Process,
            0x0F => ErrorCategory.Safety,
            _    => ErrorCategory.General,
        };
    }

    public static string ToDisplayString(this ErrorCode code)
    {
        return $"[{code.GetCategory()}] {code} (0x{(ushort)code:X4})";
    }
}
