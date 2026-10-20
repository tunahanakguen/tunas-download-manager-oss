using System.IO.Compression;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;
using TunasDM.Core.Media;

namespace TunasDM.Core.Tests;

/// <summary>Was die installierte Fassung braucht: Einzeldatei-Host, Werkzeug-Einrichtung, /exit und /tools.</summary>
public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public InstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void InstallHost_SingleFileHost_ReplacesFrameworkDependentCopy()
    {
        string app = Path.Combine(_root, "app");
        string target = Path.Combine(_root, "host");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(target);
        // Reste einer Entwicklungsfassung im Zielordner.
        foreach (string name in NativeHostRegistration.HostFiles)
            File.WriteAllText(Path.Combine(target, name), "alt " + name);
        File.WriteAllText(Path.Combine(app, "TunasDM.Host.exe"), "einzeldatei");

        string installed = NativeHostRegistration.InstallHost(app, target);

        Assert.Equal(Path.Combine(target, "TunasDM.Host.exe"), installed);
        Assert.Equal("einzeldatei", File.ReadAllText(installed));
        Assert.Equal(["TunasDM.Host.exe"], NativeHostRegistration.HostFiles.Where(f => File.Exists(Path.Combine(target, f))));
    }

    [Fact]
    public void InstallHost_WithoutHostBesideApp_ReturnsSourcePath()
    {
        string app = Path.Combine(_root, "leer");
        Directory.CreateDirectory(app);
        Assert.Equal(Path.Combine(app, "TunasDM.Host.exe"), NativeHostRegistration.InstallHost(app, Path.Combine(_root, "host")));
    }

    [Fact]
    public void FindHash_ReadsSha256SumLines()
    {
        const string sums = """
            66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a  yt-dlp.exe
            AF0156EF39B8A94274E1AF57938136FC30F18A87A923193819D4907C39C50909 *ffmpeg.zip
            kaputt  andere.exe
            """;
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", ToolSetup.FindHash(sums, "yt-dlp.exe"));
        Assert.Equal("af0156ef39b8a94274e1af57938136fc30f18a87a923193819d4907c39c50909", ToolSetup.FindHash(sums, "ffmpeg.zip"));
        Assert.Null(ToolSetup.FindHash(sums, "yt-dlp"));
        Assert.Null(ToolSetup.FindHash(sums, "andere.exe"));
    }

    [Fact]
    public void ExtractFfmpeg_TakesOnlyBinaries()
    {
        string zip = Path.Combine(_root, "ffmpeg.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string name in (string[])["ffmpeg-master/bin/ffmpeg.exe", "ffmpeg-master/bin/ffprobe.exe", "ffmpeg-master/bin/ffplay.exe", "ffmpeg-master/doc/x.html"])
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        string tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);

        ToolSetup.ExtractFfmpeg(zip, tools);

        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], Directory.GetFiles(tools).Select(Path.GetFileName).Order());
        Assert.Equal("ffmpeg-master/bin/ffmpeg.exe", File.ReadAllText(Path.Combine(tools, "ffmpeg.exe")));
    }

    [Fact]
    public void ExtractFfmpeg_MissingBinary_Throws()
    {
        string zip = Path.Combine(_root, "leer.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");
        Assert.Throws<DownloadException>(() => ToolSetup.ExtractFfmpeg(zip, _root));
    }

    [Fact]
    public void CommandLine_ExitAndTools_RoundTrip()
    {
        var request = CommandLineRequest.Parse(["/tray", "/tools"]);
        Assert.True(request.SetupTools);
        Assert.True(request.Minimized);
        Assert.False(request.IsEmpty);

        var exit = CommandLineRequest.Parse(["-exit"]);
        Assert.True(exit.Exit);
        Assert.False(exit.IsEmpty);
        Assert.Equal(exit, CommandLineRequest.Parse(exit.ToArguments()));
        Assert.Equal(request, CommandLineRequest.Parse(request.ToArguments()));
    }
}
