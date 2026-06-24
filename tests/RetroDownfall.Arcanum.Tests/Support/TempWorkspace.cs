namespace RetroDownfall.Arcanum.Tests.Support;

public sealed class TempWorkspace : IAsyncLifetime
{

    public string Root { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {

        Root = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);

        await Task.CompletedTask.ConfigureAwait(false);

    }

    public Task DisposeAsync()
    {

        if (Directory.Exists(Root))
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test directories.
            }
        }

        return Task.CompletedTask;

    }

    public string CreateSubdir(string name)
    {

        string path = Path.Combine(Root, name);

        Directory.CreateDirectory(path);

        return path;

    }

    public string WriteFile(string relativePath, string content)
    {

        string full = Path.Combine(Root, relativePath);

        string? dir = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(full, content);

        return full;

    }

}
