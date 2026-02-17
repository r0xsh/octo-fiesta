using System.Text;
using System.Text.Json;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Services.Common;

public record ExternalIdResolutionResult(
    bool Success,
    string? LocalId,
    string? ErrorMessage = null)
{
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
}

public class ExternalIdResolutionService
{
    private readonly ILocalLibraryService _localLibraryService;
    private readonly SubsonicProxyService _proxyService;
    private readonly ILogger<ExternalIdResolutionService> _logger;

    public ExternalIdResolutionService(
        ILocalLibraryService localLibraryService,
        SubsonicProxyService proxyService,
        ILogger<ExternalIdResolutionService> logger)
    {
        _localLibraryService = localLibraryService;
        _proxyService = proxyService;
        _logger = logger;
    }

    public async Task<ExternalIdResolutionResult> ResolveExternalIdAsync(
        string provider,
        string externalId,
        Dictionary<string, string> parameters,
        int maxAttempts = 20,
        int delayMs = 3000)
    {
        var mapping = await _localLibraryService.GetMappingForExternalSongAsync(provider, externalId);
        
        if (mapping?.LocalSubsonicId != null)
        {
            return new ExternalIdResolutionResult(true, mapping.LocalSubsonicId);
        }

        var title = mapping?.Title;
        var artist = mapping?.Artist;
        var album = mapping?.Album;

        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
        {
            return new ExternalIdResolutionResult(false, null, "Missing title or artist for resolution");
        }

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var scanStatus = await _localLibraryService.GetScanStatusAsync();
            
            if (scanStatus == null || !scanStatus.Scanning)
            {
                var localId = await SearchNavidromeAsync(title, artist, album, parameters);
                
                if (localId != null)
                {
                    return new ExternalIdResolutionResult(true, localId);
                }

                if (attempt < maxAttempts)
                {
                    _logger.LogDebug("Track not found, waiting for scan to complete. Attempt {Attempt}/{Max}", attempt, maxAttempts);
                    await Task.Delay(delayMs);
                }
            }
            else
            {
                if (attempt < maxAttempts)
                {
                    _logger.LogDebug("Scan in progress, waiting... Attempt {Attempt}/{Max}", attempt, maxAttempts);
                    await Task.Delay(delayMs);
                }
            }
        }

        _logger.LogWarning("Timeout waiting for scan to complete for {Provider}:{ExternalId}", provider, externalId);
        return new ExternalIdResolutionResult(false, null, "Timeout waiting for scan to complete");
    }

    private async Task<string?> SearchNavidromeAsync(
        string title, 
        string artist, 
        string? album,
        Dictionary<string, string> parameters)
    {
        var query = string.IsNullOrEmpty(album) 
            ? $"{artist} {title}" 
            : $"{artist} {album} {title}";

        var searchParams = new Dictionary<string, string>(parameters) { ["query"] = query };
        var result = await _proxyService.RelaySafeAsync("rest/search3", searchParams);
        
        if (!result.Success || result.Body == null)
            return null;

        try
        {
            var doc = JsonDocument.Parse(Encoding.UTF8.GetString(result.Body));
            var songs = doc.RootElement
                .GetProperty("subsonic-response")
                .GetProperty("searchResult3")
                .GetProperty("song");

            var targetTitle = title.ToLowerInvariant().Trim();
            var targetArtist = artist.ToLowerInvariant().Trim();
            var targetAlbum = album?.ToLowerInvariant().Trim();

            foreach (var song in songs.EnumerateArray())
            {
                var songTitle = song.GetProperty("title").GetString() ?? "";
                var songArtist = song.GetProperty("artist").GetString() ?? "";
                var songAlbum = song.GetProperty("album").GetString() ?? "";

                var normalizedTitle = songTitle.ToLowerInvariant().Trim();
                var normalizedArtist = songArtist.ToLowerInvariant().Trim();
                var normalizedAlbum = songAlbum.ToLowerInvariant().Trim();

                if (normalizedTitle != targetTitle)
                    continue;

                var artistMatches = normalizedArtist.Contains(targetArtist);
                var albumMatches = targetAlbum != null && normalizedAlbum.Contains(targetAlbum);

                if (artistMatches || albumMatches)
                    return song.GetProperty("id").GetString();
            }

            var firstSong = songs.EnumerateArray().FirstOrDefault();
            if (firstSong.ValueKind != JsonValueKind.Undefined)
            {
                _logger.LogDebug("Using first search result for {Title} by {Artist}", title, artist);
                return firstSong.GetProperty("id").GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing search results for {Title}", title);
        }

        return null;
    }

    public async Task<(byte[] Body, string? ContentType)?> ApplyStarAsync(
        string provider,
        string externalId,
        Dictionary<string, string> parameters)
    {
        var resolutionResult = await ResolveExternalIdAsync(provider, externalId, parameters);

        if (!resolutionResult.Success || string.IsNullOrEmpty(resolutionResult.LocalId))
        {
            _logger.LogWarning(
                "Could not resolve external ID {Provider}:{ExternalId}, returning success to client",
                provider, externalId);
            return null;
        }

        var starParams = new Dictionary<string, string>(parameters)
        {
            ["id"] = resolutionResult.LocalId
        };

        try
        {
            var result = await _proxyService.RelayAsync("rest/star", starParams);
            _logger.LogInformation(
                "Successfully starred external track {Provider}:{ExternalId} as local ID {LocalId}",
                provider, externalId, resolutionResult.LocalId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to star track with resolved ID {LocalId}", resolutionResult.LocalId);
            return null;
        }
    }

    public async Task<(byte[] Body, string? ContentType)?> ApplyUnstarAsync(
        string provider,
        string externalId,
        Dictionary<string, string> parameters)
    {
        var resolutionResult = await ResolveExternalIdAsync(provider, externalId, parameters);

        if (!resolutionResult.Success || string.IsNullOrEmpty(resolutionResult.LocalId))
        {
            _logger.LogWarning(
                "Could not resolve external ID {Provider}:{ExternalId}, returning success to client",
                provider, externalId);
            return null;
        }

        var unstarParams = new Dictionary<string, string>(parameters)
        {
            ["id"] = resolutionResult.LocalId
        };

        try
        {
            var result = await _proxyService.RelayAsync("rest/unstar", unstarParams);
            _logger.LogInformation(
                "Successfully unstarred external track {Provider}:{ExternalId} as local ID {LocalId}",
                provider, externalId, resolutionResult.LocalId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unstar track with resolved ID {LocalId}", resolutionResult.LocalId);
            return null;
        }
    }

    public async Task<(byte[] Body, string? ContentType)?> ApplySetRatingAsync(
        string provider,
        string externalId,
        Dictionary<string, string> parameters)
    {
        var resolutionResult = await ResolveExternalIdAsync(provider, externalId, parameters);

        if (!resolutionResult.Success || string.IsNullOrEmpty(resolutionResult.LocalId))
        {
            _logger.LogWarning(
                "Could not resolve external ID {Provider}:{ExternalId}, returning success to client",
                provider, externalId);
            return null;
        }

        var ratingParams = new Dictionary<string, string>(parameters)
        {
            ["id"] = resolutionResult.LocalId
        };

        try
        {
            var result = await _proxyService.RelayAsync("rest/setRating", ratingParams);
            _logger.LogInformation(
                "Successfully set rating for external track {Provider}:{ExternalId} as local ID {LocalId}",
                provider, externalId, resolutionResult.LocalId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set rating for track with resolved ID {LocalId}", resolutionResult.LocalId);
            return null;
        }
    }
}
