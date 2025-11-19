using System;
using System.IO;

namespace OverlayApp.Data;

internal sealed class ArcDataPaths
{
    private const string RootFolderName = "ArcRaidersHelper";
    private const string DataFolderName = "arcdata";
    private const string RepositoryFolderName = "repo";
    private const string DownloadsFolderName = "downloads";
    private const string TempFolderName = "tmp";
    private const string ProgressFileName = "progress_state.json";

    public ArcDataPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Unable to resolve LocalApplicationData path.");
        }

        RootDirectory = Path.Combine(localAppData, RootFolderName, DataFolderName);
        RepositoryDirectory = Path.Combine(RootDirectory, RepositoryFolderName);
        DownloadsDirectory = Path.Combine(RootDirectory, DownloadsFolderName);
        TempDirectory = Path.Combine(RootDirectory, TempFolderName);
        MetadataFilePath = Path.Combine(RootDirectory, "metadata.json");
        ProgressFilePath = Path.Combine(RootDirectory, ProgressFileName);
    }

    public string RootDirectory { get; }

    public string RepositoryDirectory { get; }

    public string DownloadsDirectory { get; }

    public string TempDirectory { get; }

    public string MetadataFilePath { get; }

    public string ProgressFilePath { get; }

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
