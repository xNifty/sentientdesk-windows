using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace SentientDesk.Services;

public sealed class UpdateService
{
    public const string GitHubRepoUrl = "https://github.com/xNifty/sentientdesk-windows";

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        _manager = new UpdateManager(new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            if (!_manager.IsInstalled)
                return null;

            return await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (NotInstalledException)
        {
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(UpdateInfo update)
    {
        await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
