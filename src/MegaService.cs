using CG.Web.MegaApiClient;

namespace MegaSchematicsDownloader;

/// <summary>Owns the single MegaApiClient and serializes every call to it off the UI thread.</summary>
public sealed class MegaService
{
    private readonly MegaApiClient _client = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loggedIn;

    public Task<List<INode>> GetNodesAsync(Uri folderLink) =>
        Task.Run(async () =>
        {
            await _lock.WaitAsync();
            try
            {
                if (!_loggedIn)
                {
                    _client.LoginAnonymous();
                    _loggedIn = true;
                }
                return _client.GetNodesFromLink(folderLink).ToList();
            }
            finally
            {
                _lock.Release();
            }
        });

    public Task DownloadFileAsync(INode node, string outputPath) =>
        Task.Run(async () =>
        {
            await _lock.WaitAsync();
            try
            {
                _client.DownloadFile(node, outputPath);
            }
            finally
            {
                _lock.Release();
            }
        });
}
