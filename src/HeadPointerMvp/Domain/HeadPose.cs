namespace HeadPointerMvp.Domain;

public readonly record struct HeadPose(float YawDegrees, float PitchDegrees, float RollDegrees, DateTimeOffset ObservedAt)
{
    public static HeadPose None => new(0, 0, 0, DateTimeOffset.MinValue);
}

public enum Direction
{
    Left,
    Center,
    Right
}
