using Microsoft.Data.Sqlite;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;
using TunasDM.Core.Media;

namespace TunasDM.Core.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public MediaTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Auszug im Aufbau von „yt-dlp -J“ (YouTube): getrennte Bild- und Tonspuren.
    private const string YouTubeJson = """
        {
          "title": "Me at the zoo", "duration": 19, "extractor_key": "Youtube",
          "webpage_url": "https://www.youtube.com/watch?v=jNQXAC9IVRw",
          "thumbnail": "https://i.ytimg.com/vi/jNQXAC9IVRw/maxresdefault.jpg",
          "formats": [
            { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 309288, "tbr": 129.8 },
            { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "filesize": 252182, "tbr": 106.1 },
            { "format_id": "160", "ext": "mp4", "height": 144, "vcodec": "avc1.4D400B", "acodec": "none", "filesize": 195278, "tbr": 82.5 },
            { "format_id": "133", "ext": "mp4", "height": 240, "vcodec": "avc1.4D400C", "acodec": "none", "filesize": 433081, "tbr": 183.0 },
            { "format_id": "242", "ext": "webm", "height": 240, "vcodec": "vp9", "acodec": "none", "filesize": 293080, "tbr": 123.8 },
            { "format_id": "399", "ext": "mp4", "height": 1080, "vcodec": "av01.0.08M.08", "acodec": "none", "tbr": 1500.0 }
          ]
        }
        """;

    [Fact]
    public void Parse_BuildsQualitiesFromHighestToLowest_PlusAudio()
    {
        var info = YtDlp.Parse(YouTubeJson);

        Assert.Equal("Me at the zoo", info.Title);
        Assert.Equal(19, info.Duration);
        Assert.Equal(["v1080", "v240", "v144", "a-m4a", "a-mp3"], info.Options.Select(o => o.Code));
        Assert.Equal("1080p · HD", info.Options[0].Label);
        Assert.Equal("MP4 · AV1", info.Options[0].Detail);
        Assert.Equal("MP4 · H.264", info.Options[1].Detail);
    }

    [Fact]
    public void Parse_EstimatesSize_VideoPlusBestM4aAudio()
    {
        var info = YtDlp.Parse(YouTubeJson);

        // 240p: H.264-Spur (433081) bevorzugt, plus M4A-Ton (309288).
        Assert.Equal(433081 + 309288, info.Options.Single(o => o.Code == "v240").EstimatedSize);
        // 1080p ohne Dateigröße: aus Bitrate × Dauer geschätzt.
        Assert.Equal((long)(1500.0 * 1000 / 8 * 19) + 309288, info.Options.Single(o => o.Code == "v1080").EstimatedSize);
        Assert.Equal(309288, info.Options.Single(o => o.Code == "a-m4a").EstimatedSize);
    }

    [Fact]
    public void Parse_DirectFileWithoutFormats_OffersBestQuality()
    {
        var info = YtDlp.Parse("""{ "title": "clip", "ext": "mp4" }""");
        Assert.Equal(YtDlp.BestCode, Assert.Single(info.Options).Code);
    }

    [Theory]
    [InlineData("v720", "bv*[height<=720]+ba/b[height<=720]/b", "mp4")]
    [InlineData("best", "bv*+ba/b", "mp4")]
    public void FormatArguments_Video_PreferH264AndMergeToMp4(string code, string selector, string container)
    {
        var args = YtDlp.FormatArguments(code);
        Assert.Equal(selector, args[args.ToList().IndexOf("-f") + 1]);
        Assert.Equal("res,vcodec:h264,acodec:m4a", args[args.ToList().IndexOf("-S") + 1]);
        Assert.Equal(container, args[args.ToList().IndexOf("--merge-output-format") + 1]);
    }

    [Fact]
    public void FormatArguments_Mp3_ExtractsAudio()
    {
        var args = YtDlp.FormatArguments(YtDlp.AudioMp3Code);
        Assert.Contains("-x", args);
        Assert.Equal("mp3", args[args.ToList().IndexOf("--audio-format") + 1]);
    }

    [Fact]
    public void MediaDownload_SumsProgressOfVideoAndAudioStreams()
    {
        var download = new MediaDownload("https://www.youtube.com/watch?v=x", _directory, "Clip: Test?", "v720");

        download.HandleLine("TUNAS|137|1000|4000|NA");
        download.HandleLine("TUNAS|140|500|NA|1000");
        download.HandleLine("[download] irgendwas anderes");
        download.HandleLine("TUNAS|137|2000|4000|NA");

        var progress = download.GetProgress();
        Assert.Equal(2500, progress.Downloaded);
        Assert.Equal(5000, progress.TotalSize);
        Assert.Equal("Clip_ Test_", download.BaseName);

        download.HandleLine("TUNASFILE|" + Path.Combine(_directory, "Clip_ Test_.mp4"));
        Assert.Equal(Path.Combine(_directory, "Clip_ Test_.mp4"), download.FilePath);
    }

    [Fact]
    public void MediaDownload_BuildsArgumentsWithOutputTemplateAndLimit()
    {
        var download = new MediaDownload("https://vimeo.com/1", _directory, "film", "v480", referrer: "https://seite.de/", limiter: new SpeedLimiter(500_000));

        var args = download.BuildArguments(@"C:\tools").ToList();

        Assert.Equal(Path.Combine(_directory, "film.%(ext)s"), args[args.IndexOf("-o") + 1]);
        Assert.Equal(@"C:\tools", args[args.IndexOf("--ffmpeg-location") + 1]);
        Assert.Equal("500000", args[args.IndexOf("--limit-rate") + 1]);
        Assert.Equal("https://seite.de/", args[args.IndexOf("--referer") + 1]);
        Assert.Equal("https://vimeo.com/1", args[^1]);
    }

    [Fact]
    public void Database_MigratesOldSchema_AndStoresMediaFields()
    {
        string path = Path.Combine(_directory, "alt.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE downloads (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, file_name TEXT, directory TEXT NOT NULL,
                    file_path TEXT, category TEXT NOT NULL, status INTEGER NOT NULL, total_size INTEGER NOT NULL, downloaded INTEGER NOT NULL,
                    supports_ranges INTEGER NOT NULL, error TEXT, added_at TEXT NOT NULL, completed_at TEXT, referrer TEXT, cookies TEXT,
                    user_agent TEXT, description TEXT);
                INSERT INTO downloads (url, directory, category, status, total_size, downloaded, supports_ranges, added_at)
                    VALUES ('https://example.com/a.zip', 'C:\x', 'Archive', 4, 10, 10, 1, '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var database = new DownloadDatabase(path);
        var old = Assert.Single(database.LoadAll());
        Assert.Equal(DownloadKind.File, old.Kind);

        var media = new DownloadItem { Url = "https://youtu.be/x", Directory = _directory, Kind = DownloadKind.Media, MediaFormat = "a-mp3" };
        database.Insert(media);
        var loaded = database.LoadAll().Single(i => i.Id == media.Id);
        Assert.Equal((DownloadKind.Media, "a-mp3"), (loaded.Kind, loaded.MediaFormat));
    }

    [Fact]
    public void MediaMessage_OrdersCandidates_FrameThenPageThenStreams()
    {
        var message = BrowserProtocol.Parse("""
            {"type":"media","url":"https://www.youtube.com/embed/abc","pageUrl":"https://www.bing.com/videos/x",
             "streams":["blob:https://x/1","https://cdn.example.com/master.m3u8","https://www.youtube.com/embed/abc"]}
            """.ReplaceLineEndings(""))!;

        Assert.Equal(["https://www.youtube.com/embed/abc", "https://www.bing.com/videos/x", "https://cdn.example.com/master.m3u8"],
            message.MediaCandidates().Select(u => u.ToString()));
    }

    [Fact]
    public void MediaMessage_PutsDirectSourceOfClickedVideoFirst()
    {
        var message = BrowserProtocol.Parse("""
            {"type":"media","src":"https://cdn.example.com/flower.mp4","url":"https://seite.de/","pageUrl":"https://seite.de/"}
            """)!;

        Assert.Equal(["https://cdn.example.com/flower.mp4", "https://seite.de/"], message.MediaCandidates().Select(u => u.ToString()));
    }

    [Fact]
    public void Parse_PageWithSeveralVideos_IsNoSingleChoice()
    {
        var info = YtDlp.Parse("""{ "_type": "playlist", "title": "Seite", "entries": [ { "title": "a" }, { "title": "b" } ] }""");
        Assert.Empty(info.Options);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("https://youtu.be/abc", true)]
    [InlineData("https://m.youtube.com/watch?v=abc", true)]
    [InlineData("https://cdn.example.com/video/master.m3u8?token=1", true)]
    [InlineData("https://example.com/setup.exe", false)]
    [InlineData("https://notyoutube.com/x", false)]
    public void MediaSites_RecognizesVideoPages(string url, bool expected)
    {
        Assert.Equal(expected, MediaSites.IsMediaPage(new Uri(url)));
    }

    [Fact]
    public void Config_ContainsVideoButtonSwitch()
    {
        Assert.True(BrowserProtocol.CreateConfig(new AppSettings()).VideoButton);
        Assert.False(BrowserProtocol.CreateConfig(new AppSettings { ShowVideoButton = false }).VideoButton);
    }

    // ---------- Beste Fassung aus mehreren Kandidaten ----------

    private static MediaInfo Info(string title, params int[] heights) => new(title, 60, null, "Generic", "",
        heights.Select(h => new MediaOption($"v{h}", $"{h}p", "MP4", null, IsAudio: false)).ToList());

    private static Func<Uri, CancellationToken, Task<MediaInfo>> FakeProbe(Dictionary<string, (int DelayMs, MediaInfo? Info)> answers) =>
        async (url, token) =>
        {
            var (delay, info) = answers[url.ToString()];
            await Task.Delay(delay, token);
            return info ?? throw new DownloadException("kein Video");
        };

    [Fact]
    public async Task ProbeBest_PrefersHighestResolution_NotFirstAnswer()
    {
        var answers = new Dictionary<string, (int, MediaInfo?)>
        {
            ["https://player.example/embed/1"] = (10, Info("228747261_240p", 240)),
            ["https://cdn.example/master.m3u8"] = (80, Info("master", 480, 240)),
            ["https://seite.example/video"] = (60, null),
        };
        var choice = await MediaProbe.ProbeBestAsync(answers.Keys.Select(k => new Uri(k)).ToList(), FakeProbe(answers),
            TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("https://cdn.example/master.m3u8", choice!.Url.ToString());
        Assert.Equal(480, MediaProbe.MaxHeight(choice.Info));
    }

    [Fact]
    public async Task ProbeBest_KnownSiteWinsImmediately()
    {
        var answers = new Dictionary<string, (int, MediaInfo?)>
        {
            ["https://www.youtube.com/watch?v=x"] = (10, Info("Echter Titel", 360)),
            ["https://cdn.example/other.m3u8"] = (5000, Info("x", 2160)),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var choice = await MediaProbe.ProbeBestAsync(answers.Keys.Select(k => new Uri(k)).ToList(), FakeProbe(answers),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("www.youtube.com", choice!.Url.Host);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProbeBest_WaitsOnlyBrieflyAfterFirstHit_AndReportsErrors()
    {
        var answers = new Dictionary<string, (int, MediaInfo?)>
        {
            ["https://a.example/1"] = (10, Info("schnell", 360)),
            ["https://b.example/2"] = (5000, Info("langsam", 1080)),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var choice = await MediaProbe.ProbeBestAsync(answers.Keys.Select(k => new Uri(k)).ToList(), FakeProbe(answers),
            TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Equal("https://a.example/1", choice!.Url.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));

        var errors = new List<string>();
        var none = new Dictionary<string, (int, MediaInfo?)> { ["https://c.example/3"] = (5, null) };
        Assert.Null(await MediaProbe.ProbeBestAsync([new Uri("https://c.example/3")], FakeProbe(none), TimeSpan.FromSeconds(1),
            CancellationToken.None, errors.Add));
        Assert.Equal(["kein Video"], errors);
    }

    [Theory]
    [InlineData("https://cdn.example/master.m3u8", "(3) [ABC-123] Ein Titel - Seite", "[ABC-123] Ein Titel - Seite")]
    [InlineData("https://www.youtube.com/watch?v=x", "(3) Ich im Zoo - YouTube", "yt-dlp-Titel")]
    [InlineData("https://cdn.example/master.m3u8", "  ", "yt-dlp-Titel")]
    public void ChooseTitle_PageTitleForUnknownSites(string url, string pageTitle, string expected)
    {
        var choice = new MediaChoice(new Uri(url), Info("yt-dlp-Titel", 480));
        Assert.Equal(expected, MediaProbe.ChooseTitle(choice, pageTitle));
    }
}
