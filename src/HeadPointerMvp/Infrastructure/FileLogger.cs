namespace HeadPointerMvp.Infrastructure;

public sealed class FileLogger
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task InfoAsync(string message)
    {
        AppPaths.EnsureCreated();
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        await _gate.WaitAsync();
        try { await File.AppendAllTextAsync(AppPaths.LogFile, line); }
        finally { _gate.Release(); }
    }
}
