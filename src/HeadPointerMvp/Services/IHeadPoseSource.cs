using HeadPointerMvp.Domain;

namespace HeadPointerMvp.Services;

public interface IHeadPoseSource : IAsyncDisposable
{
    event EventHandler<HeadPose>? PoseReceived;
    Task StartAsync(CancellationToken cancellationToken);
}
