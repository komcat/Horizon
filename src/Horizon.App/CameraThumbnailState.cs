using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Horizon.Camera;

namespace Horizon.App;

internal sealed class CameraThumbnailState
{
    public required string CameraId { get; init; }
    public required string DisplayName { get; init; }
    public LiveVideoSubscriber? Subscriber { get; set; }
    public WriteableBitmap? Bitmap { get; set; }
    public required Image ImageElement { get; init; }
    public required Border ContainerBorder { get; init; }
    public bool IsActive { get; set; }
}
