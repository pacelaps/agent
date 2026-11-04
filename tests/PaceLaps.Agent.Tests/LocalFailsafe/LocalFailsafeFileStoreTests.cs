using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Core.LocalFailsafe;

namespace PaceLaps.Agent.Tests.LocalFailsafe;

public sealed class LocalFailsafeFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "irc-lfs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_copy_is_saved_read_back_and_deleted()
    {
        var store = new LocalFailsafeFileStore(Path.Combine(_dir, "local-failsafe.json"));
        var copy = new LocalFailsafeCopy("local-failsafe-default", 2, new string('a', 64), true, false,
            LocalFailsafeCopiesTests.TemplateDocument().GetRawText());

        Assert.Null(store.Load());
        store.Save(copy);
        var read = new LocalFailsafeFileStore(store.Path).Load();

        Assert.Equal(copy, read);
        Assert.NotNull(read!.Read());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void A_damaged_file_counts_as_no_copy()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "local-failsafe.json");
        File.WriteAllText(path, "{not json");

        Assert.Null(new LocalFailsafeFileStore(path).Load());
    }
}
