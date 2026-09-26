using System.Diagnostics;
using System.Text.Json;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// scip-python names a library symbol by the package whose files contain it. Left to find those
/// files itself on Windows, it runs its lookup under `python3` - which a virtual environment does
/// not have - then falls back to a `pip show` that times out on a busy machine. The list comes
/// back empty on some runs and full on others, and a thousand library lines break and rejoin from
/// one snapshot to the next with no change to the code. Treering hands it the list instead.
/// </summary>
public sealed class PythonPackagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tr-py-{Guid.NewGuid():N}");

    public PythonPackagesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private string Make(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static ProcessStartInfo Indexing() => new("scip-python") { ArgumentList = { "index", "." } };

    /// <summary>A Python that really runs, or null on a machine without one.</summary>
    private static string? RealPython()
    {
        foreach (var name in new[] { "python", "py" })
        {
            var found = Executables.Find(name);
            if (!Path.IsPathRooted(found)) continue;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(found, "-c \"print(1)\"") { RedirectStandardOutput = true, RedirectStandardError = true });
                if (process is not null && process.WaitForExit(10_000) && process.ExitCode == 0) return found;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }

        return null;
    }

    [Fact]
    public void The_repositorys_own_virtual_environment_answers_for_its_packages()
    {
        var python = Make(OperatingSystem.IsWindows() ? ".venv/Scripts/python.exe" : ".venv/bin/python");

        Assert.Equal(python, PythonIndexing.PythonFor(_root));
    }

    /// <summary>
    /// A real virtual environment in the repo, with one package installed the way pip leaves it:
    /// the code, and a dist-info folder whose RECORD lists the files. Null without a Python.
    /// </summary>
    private string? VenvWithOnePackage()
    {
        var python = RealPython();
        if (python is null) return null;

        var arguments = Path.GetFileName(python).StartsWith("py.", StringComparison.OrdinalIgnoreCase) ? "-3 " : string.Empty;
        using (var venv = Process.Start(new ProcessStartInfo(python, $"{arguments}-m venv --without-pip \"{Path.Combine(_root, ".venv")}\"") { RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            if (venv is null || !venv.WaitForExit(60_000) || venv.ExitCode != 0) return null;
        }

        var venvPython = PythonIndexing.PythonFor(_root);
        if (venvPython is null) return null;
        using var ask = Process.Start(new ProcessStartInfo(venvPython, "-c \"import sysconfig; print(sysconfig.get_paths()['purelib'])\"") { RedirectStandardOutput = true });
        var sitePackages = ask!.StandardOutput.ReadToEnd().Trim();
        ask.WaitForExit();

        Directory.CreateDirectory(Path.Combine(sitePackages, "shop_lib"));
        File.WriteAllText(Path.Combine(sitePackages, "shop_lib", "__init__.py"), "class Order: ...\n");
        var info = Path.Combine(sitePackages, "shop_lib-1.2.dist-info");
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Combine(info, "METADATA"), "Metadata-Version: 2.1\nName: shop-lib\nVersion: 1.2\n");
        File.WriteAllText(Path.Combine(info, "RECORD"),
            "shop_lib/__init__.py,,\nshop_lib/__pycache__/__init__.cpython-314.pyc,,\nshop_lib/py.typed,,\nshop_lib-1.2.dist-info/METADATA,,\n");
        return venvPython;
    }

    [Fact]
    public void The_list_is_handed_to_the_indexer_so_it_does_not_look_for_itself()
    {
        var python = VenvWithOnePackage();
        if (python is null) return;   // nothing to build an environment with on this machine
        var file = Path.Combine(_root, "out.scip.packages.json");
        var start = Indexing();

        PythonIndexing.PinPackages(start, python, file);

        var arguments = start.ArgumentList.ToList();
        Assert.Equal(file, arguments[arguments.IndexOf("--environment") + 1]);

        // The shape scip-python reads back: name, version, and only the package's Python files.
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var package = Assert.Single(document.RootElement.EnumerateArray(), item => item.GetProperty("name").GetString() == "shop-lib");
        Assert.Equal("1.2", package.GetProperty("version").GetString());
        Assert.Equal(["shop_lib/__init__.py"], package.GetProperty("files").EnumerateArray().Select(path => path.GetString()));
    }

    [Fact]
    public void When_the_list_cannot_be_made_the_indexer_is_left_as_it_was()
    {
        var file = Path.Combine(_root, "out.scip.packages.json");
        var start = Indexing();

        PythonIndexing.PinPackages(start, Path.Combine(_root, "no-such-python.exe"), file);

        Assert.DoesNotContain("--environment", start.ArgumentList);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void An_environment_already_chosen_is_not_given_a_second_one()
    {
        // Without pip anywhere, the command already says "no packages". Two --environment
        // flags would leave it to the indexer which one wins.
        var python = RealPython();
        if (python is null) return;
        var start = Indexing();
        start.ArgumentList.Add("--environment");
        start.ArgumentList.Add("no-packages.json");

        PythonIndexing.PinPackages(start, python, Path.Combine(_root, "out.scip.packages.json"));

        Assert.Single(start.ArgumentList, argument => argument == "--environment");
    }
}
