using System.Diagnostics;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// Which files changed decides what `update`, `watch` and the server's own watcher re-index.
/// A file missed here is a change the map never shows; a file read wrongly sends the re-index
/// to the wrong project, or to none. These run against a real git repository.
/// </summary>
public sealed class ChangedFilesTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"tr-git-{Guid.NewGuid():N}");

    public ChangedFilesTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        Write("shop/orders.py", "class Order: ...\n");
        Write("README.md", "# shop\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "start");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* a temp folder left behind is harmless */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    [Fact]
    public void An_edited_source_file_not_yet_committed_is_seen()
    {
        Write("shop/orders.py", "class Order:\n    qty = 1\n");

        Assert.Contains("shop/orders.py", Incremental.ChangedFiles(_repo, since: null).Select(Slashes));
    }

    [Fact]
    public void A_new_source_file_not_yet_added_is_seen()
    {
        Write("shop/cart.py", "class Cart: ...\n");

        Assert.Contains("shop/cart.py", Incremental.ChangedFiles(_repo, since: null).Select(Slashes));
    }

    [Fact]
    public void A_file_no_indexer_reads_is_left_out()
    {
        Write("README.md", "# shop, edited\n");

        Assert.Empty(Incremental.ChangedFiles(_repo, since: null));
    }

    [Fact]
    public void Files_changed_between_a_commit_and_head_are_seen()
    {
        var start = Git("rev-parse", "HEAD");
        Write("shop/cart.py", "class Cart: ...\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "cart");

        Assert.Equal(["shop/cart.py"], Incremental.ChangedFiles(_repo, since: start).Select(Slashes));
    }

    [Fact]
    public void A_renamed_file_is_seen_under_its_new_name_and_its_old_one()
    {
        // git status writes a rename as "R  old -> new" on one line. Read as one path, it names a
        // file that does not exist - and the watcher, which fingerprints files by their write
        // time, then never notices the next edit to it. The old path counts too: its project lost
        // whatever the file held.
        Git("mv", "shop/orders.py", "shop/order_book.py");

        var changed = Incremental.ChangedFiles(_repo, since: null).Select(Slashes).ToList();

        Assert.Contains("shop/order_book.py", changed);
        Assert.Contains("shop/orders.py", changed);
        Assert.DoesNotContain(changed, path => path.Contains("->"));
    }

    [Fact]
    public void A_file_with_a_non_ascii_name_is_seen_by_its_real_name()
    {
        // By default git escapes such names ("ì£¼.py"), which is not a file either.
        Write("shop/주문.py", "class Order: ...\n");

        Assert.Contains("shop/주문.py", Incremental.ChangedFiles(_repo, since: null).Select(Slashes));
    }

    [Fact]
    public void A_non_ascii_name_changed_between_commits_is_seen_by_its_real_name()
    {
        var start = Git("rev-parse", "HEAD");
        Write("shop/주문.py", "class Order: ...\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "order");

        Assert.Equal(["shop/주문.py"], Incremental.ChangedFiles(_repo, since: start).Select(Slashes));
    }

    [Fact]
    public void A_file_renamed_between_commits_counts_under_both_names()
    {
        var start = Git("rev-parse", "HEAD");
        Git("mv", "shop/orders.py", "shop/order_book.py");
        Git("commit", "-q", "-m", "rename");

        Assert.Equal(["shop/order_book.py", "shop/orders.py"], Incremental.ChangedFiles(_repo, since: start).Select(Slashes).Order());
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_index()
    {
        Assert.Empty(Incremental.ChangedFiles(_repo, since: null));
    }
}
