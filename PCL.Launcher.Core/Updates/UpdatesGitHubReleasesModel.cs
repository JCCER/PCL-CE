using System.IO.Compression;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     以 GitHub Releases 作为更新源：读取自己仓库（JCCER/PCL-CE）的最新 Release。
///     配合 GitHub Actions 自动编译并发布 Release 即可实现个人自动更新。
/// </summary>
public class UpdatesGitHubReleasesModel : IUpdateSource
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/JCCER/PCL-CE/releases/latest";

    private JsonObject? _release;
    private string? _assetDownloadUrl;
    private string? _assetName;

    public string SourceName { get; set; } = "GitHub Releases";

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        using var response = HttpRequest
            .Create(ReleaseApiUrl)
            .SendAsync()
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();
        _release = (JsonObject)LauncherCommon.GetJson(response.AsString());
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        if (_release is null)
            RefreshCache();

        var tag = _release?["tag_name"]?.ToString()?.TrimStart('v') ?? "0.0.0";
        var body = _release?["body"]?.ToString() ?? "";
        var asset = _SelectAsset(arch);
        _assetDownloadUrl = asset?["browser_download_url"]?.ToString();
        _assetName = asset?["name"]?.ToString();

        var sha256 = "";
        var digest = asset?["digest"]?.ToString();
        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha256 = digest["sha256:".Length..];

        return new VersionDataModel
        {
            VersionName = tag,
            VersionCode = 0,
            Changelog = body,
            Sha256 = sha256,
            Source = SourceName
        };
    }

    public bool IsLatest(
        UpdateChannel channel,
        UpdateArch arch,
        SemVer currentVersion,
        int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latest.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = [] };

    public List<Loading.LoaderBase> GetDownloadLoader(
        UpdateChannel channel,
        UpdateArch arch,
        string output)
    {
        if (_assetDownloadUrl is null)
            GetLatestVersion(channel, arch);

        var downloadUrl = _assetDownloadUrl
            ?? throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        var isZip = _assetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
        var tempPath = $@"{LauncherPaths.Temp}CEUpdate-{_assetName ?? "download"}";

        var loaders = new List<Loading.LoaderBase>
        {
            new Loading.LoaderTask<int, List<DownloadFile>>(
                Lang.Text("Update.Task.GetVersionInfo"),
                load => { load.output = [new DownloadFile([downloadUrl], tempPath)]; }),
            new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), [])
        };

        loaders.Add(
            new Loading.LoaderTask<string, int>(
                Lang.Text("Update.Task.ApplyFile"),
                _ =>
                {
                    if (isZip)
                    {
                        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var zip = new ZipArchive(fs);
                        var entry = zip.Entries
                                .FirstOrDefault(x => x.Name.Contains("Plain Craft Launcher Community Edition.exe"))
                            ?? zip.Entries.FirstOrDefault(x => x.Name.Contains("Plain Craft Launcher"))
                            ?? zip.Entries.FirstOrDefault(x => x.Name.Contains(".exe"));
                        if (entry is null)
                            throw new Exception(Lang.Text("Update.Error.FileNotFound"));
                        entry.ExtractToFile(output, true);
                    }
                    else
                    {
                        LauncherCommon.CopyFile(tempPath, output);
                    }
                }));

        return loaders;
    }

    private JsonObject? _SelectAsset(UpdateArch arch)
    {
        var assets = _release?["assets"]?.AsArray();
        if (assets is null)
            return null;

        var archToken = arch == UpdateArch.Arm64 ? "arm64" : "x64";
        JsonObject? fallback = null;
        foreach (var node in assets)
        {
            if (node is not JsonObject asset)
                continue;
            var name = asset["name"]?.ToString() ?? "";
            if (name.IndexOf(archToken, StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("source", StringComparison.OrdinalIgnoreCase) < 0
                && !name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                return asset;
            if (fallback is null
                && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                fallback = asset;
        }
        return fallback;
    }
}
