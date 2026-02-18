namespace SPMResolver.Services;

public sealed class TemporaryWorkspace : IDisposable
{
    private bool _disposed;
    private readonly bool _keepAfterCompletion;

    private TemporaryWorkspace(string rootPath, bool keepAfterCompletion)
    {
        _keepAfterCompletion = keepAfterCompletion;
        RootPath = rootPath;
        PackagePath = Path.Combine(rootPath, "package");
        ScratchPath = Path.Combine(rootPath, "scratch");

        Directory.CreateDirectory(PackagePath);
        Directory.CreateDirectory(ScratchPath);
    }

    public string RootPath { get; }

    public string PackagePath { get; }

    public string ScratchPath { get; }

    public static TemporaryWorkspace Create(bool keepAfterCompletion = false)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "spm-resolver", Guid.NewGuid().ToString("N"));
        return new TemporaryWorkspace(rootPath, keepAfterCompletion);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_keepAfterCompletion && Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        finally
        {
            _disposed = true;
        }
    }
}
