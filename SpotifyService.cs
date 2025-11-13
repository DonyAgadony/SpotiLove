using Microsoft.EntityFrameworkCore;
using SpotifyAPI.Web;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spotilove;

public class SpotifyService
{
    private SpotifyClient? _spotify;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;

    /// <summary>
    /// A record to hold artist information including their name and image URL.
    /// </summary>
    public record ArtistInfo(string Name, string? ImageUrl);

    private string? _refreshToken; // Used to refresh tokens without user interaction

    public SpotifyService(string clientId, string clientSecret, string redirectUri)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
    }

    // --- AUTHENTICATION & CLIENT INITIALIZATION ---

    public string GetLoginUrl()
    {
        var request = new LoginRequest(
            new Uri(_redirectUri),
            _clientId,
            LoginRequest.ResponseType.Code)
        {
            Scope = new[]
            {
                Scopes.UserReadEmail,
                Scopes.UserReadPrivate,
                Scopes.UserTopRead, // Crucial for fetching top artists/songs/genres
                Scopes.UserReadPlaybackState // Example for future features
            },
            // Note: In a production app, you would generate and check the state parameter for CSRF protection.
        };

        return request.ToUri().ToString();
    }

    public async Task ConnectUserAsync(string code)
    {
        var oauth = new OAuthClient();
        var tokenResponse = await oauth.RequestToken(
            new AuthorizationCodeTokenRequest(_clientId, _clientSecret, code, new Uri(_redirectUri))
        );

        _spotify = new SpotifyClient(tokenResponse.AccessToken);
        _refreshToken = tokenResponse.RefreshToken;
    }

    public async Task RefreshAccessTokenAsync()
    {
        if (_refreshToken == null)
        {
            // Throwing an exception forces the user to re-authenticate via the login URL.
            throw new Exception("No refresh token available. User must re-login.");
        }

        var oauth = new OAuthClient();
        var response = await oauth.RequestToken(
            new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, _refreshToken)
        );

        _spotify = new SpotifyClient(response.AccessToken);
        // The refresh token may be renewed, so we update it if a new one is provided.
        _refreshToken = response.RefreshToken ?? _refreshToken;
    }

    /// <summary>
    /// Ensures the Spotify client is authenticated, using client credentials as a fallback.
    /// This is useful for non-user-specific requests like searching or browsing public playlists.
    /// </summary>
    private async Task EnsureClientIsAuthenticatedAsync()
    {
        // If we already have a user-authenticated client, we're good.
        if (_spotify != null) return;

        // Otherwise, authenticate using client credentials (app-level authentication).
        var config = SpotifyClientConfig.CreateDefault();
        var request = new ClientCredentialsRequest(_clientId, _clientSecret);
        var response = await new OAuthClient(config).RequestToken(request);

        _spotify = new SpotifyClient(response.AccessToken);
    }

    // ---------------------------
    // SPOTIFY DATA FETCHING
    // ---------------------------

    public async Task<PrivateUser> GetUserProfileAsync()
    {
        if (_spotify == null) throw new Exception("User not connected");
        return await _spotify.UserProfile.Current();
    }

    /// Fetches the user's top artists for the short term (last 4 weeks).
    public async Task<List<ArtistInfo>> GetUserTopArtistsWithImagesAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topArtists = await _spotify.Personalization.GetTopArtists(new PersonalizationTopRequest
        {
            Limit = limit,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm,
        });

        return topArtists.Items?
            .Select(a => new ArtistInfo(a.Name, a.Images.FirstOrDefault()?.Url))
            .ToList()
            ?? new List<ArtistInfo>();
    }

    /// Fetches the user's top tracks for the short term (last 4 weeks), formatted as "Song by Artist".
    public async Task<List<string>> GetUserTopSongsAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topTracks = await _spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
        {
            Limit = limit,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm
        });

        return topTracks.Items?.Select(t => $"{t.Name} by {string.Join(", ", t.Artists.Select(a => a.Name))}").ToList() ?? new List<string>();
    }

    /// Fetches the genres of the user's top artists, used for compatibility scoring.
    public async Task<List<string>> GetUserTopGenresAsync(int limit = 20)
    {
        if (_spotify == null) throw new Exception("User not connected");

        // We fetch more artists (up to 50 is common practice) to get a richer genre set
        var topArtists = await _spotify.Personalization.GetTopArtists(new PersonalizationTopRequest
        {
            Limit = 50,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm
        });

        var genres = new List<string>();
        if (topArtists.Items != null)
        {
            foreach (var artist in topArtists.Items)
            {
                if (artist.Genres != null)
                {
                    genres.AddRange(artist.Genres);
                }
            }
        }

        // Return a distinct list, taking only the top 'limit' most frequent ones.
        return genres.GroupBy(g => g)
                     .OrderByDescending(g => g.Count())
                     .Select(g => g.Key)
                     .Take(limit)
                     .ToList();
    }

    public async Task<List<ArtistInfo>> SearchArtistsAsync(string query, int limit = 10)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null) throw new Exception("Could not authenticate with Spotify");

        var searchRequest = new SearchRequest(SearchRequest.Types.Artist, query) { Limit = limit };
        var searchResponse = await _spotify.Search.Item(searchRequest);

        return searchResponse.Artists.Items?
            .Select(a => new ArtistInfo(a.Name, a.Images.FirstOrDefault()?.Url))
            .ToList()
            ?? new List<ArtistInfo>();
    }

    /// <summary>
    /// Fetches popular artists by reading the artists from a global playlist and fetching their full profiles.
    /// FIXED: Uses Task.WhenAll to fetch multiple artist profiles concurrently and non-blockingly.
    /// </summary>
    public async Task<List<ArtistInfo>> GetPopularArtistsAsync(int limit = 20)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null)
            throw new Exception("Could not authenticate with Spotify");

        try
        {
            // ID for the global "Today's Top Hits" playlist on Spotify
            const string topHitsPlaylistId = "37i9dQZF1DXcBWIGoYBM5M";

            // 1. Fetch the playlist
            var playlist = await _spotify.Playlists.Get(topHitsPlaylistId);

            // 2. Extract unique artist IDs
            var artistIds = playlist.Tracks?.Items?
                .SelectMany(item => item.Track is FullTrack track ? track.Artists : Enumerable.Empty<SimpleArtist>())
                .Where(artist => artist != null && !string.IsNullOrEmpty(artist.Id))
                .Select(artist => artist.Id)
                .Distinct()
                .Take(limit) // Limit the number of unique IDs we will process
                .ToList(); // Materialize the list

            if (artistIds == null || !artistIds.Any())
            {
                return new List<ArtistInfo>();
            }

            // 3. Create a list of tasks for fetching full artist details
            var artistTasks = artistIds.Select(id => _spotify.Artists.Get(id)).ToList();

            // 4. Await all tasks concurrently and non-blockingly
            var fullArtists = await Task.WhenAll(artistTasks);

            // 5. Map the resulting FullArtist objects to your ArtistInfo record
            return fullArtists
                .Where(a => a != null) // Filter out any artists that might have failed to load
                .Select(fullArtist => new ArtistInfo(fullArtist.Name, fullArtist.Images.FirstOrDefault()?.Url))
                .ToList();
        }
        catch (APIException ex)
        {
            // Catching Spotify API specific errors (e.g., 404 Not Found, 401 Unauthorized, etc.)
            Console.WriteLine($"Spotify API Error in GetPopularArtistsAsync: {ex.Response?.StatusCode} - {ex.Message}");
            // Return empty list gracefully instead of crashing the API
            return new List<ArtistInfo>();
        }
        catch (Exception ex)
        {
            // Catching other unexpected exceptions
            Console.WriteLine($"Unexpected Error in GetPopularArtistsAsync: {ex.Message}");
            return new List<ArtistInfo>();
        }
    }

    //helper function
    private string CreateSlug(string input)
    {
        // Simple slugification, removing common punctuation and replacing spaces with hyphens.
        return input.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(".", "")
            .Replace(",", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("&", "and");
    }

    /// <summary>
    /// Fetches top songs and returns them as slugs (song-artist) for easier database comparison.
    /// </summary>
    public async Task<List<string>> GetUserTopSongsSlugsAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topTracks = await _spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
        {
            Limit = limit,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm
        });

        // Creates a slug like "running-up-that-hill-by-kate-bush"
        return topTracks.Items?.Select(t => CreateSlug($"{t.Name}-by-{t.Artists.FirstOrDefault()?.Name}")).ToList() ?? new List<string>();
    }


    // ---------------------------
    // SAVE TO DB (Integration with AppDbContext)
    // ---------------------------

    /// <summary>
    /// Fetches all top data from Spotify and updates the local User and MusicProfile entities.
    /// </summary>
    public async Task<User> UpdateUserProfileInDb(AppDbContext db, int userId)
    {
        if (_spotify == null) throw new Exception("User not connected to Spotify");

        // 1. Fetch data from Spotify
        var profile = await GetUserProfileAsync();
        var songs = await GetUserTopSongsAsync();
        var artistsWithImages = await GetUserTopArtistsWithImagesAsync();
        var genres = await GetUserTopGenresAsync();

        // Use song slugs for slightly more robust song matching/storage
        var songSlugs = await GetUserTopSongsSlugsAsync();


        // 2. Load user and music profile from database (uses the models in Spotilove.cs)
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            throw new Exception($"User with ID {userId} not found in local database.");

        // 3. Update User details (Name, Email from Spotify)
        // Only set name if it's currently empty (e.g., first time login)
        if (string.IsNullOrEmpty(user.Name))
        {
            user.Name = profile.DisplayName ?? profile.Id;
        }
        // Update email if available
        user.Email = profile.Email ?? user.Email;

        // 4. Update or Create MusicProfile
        if (user.MusicProfile == null)
        {
            user.MusicProfile = new MusicProfile
            {
                FavoriteSongs = string.Join(", ", songs),
                FavoriteArtists = JsonSerializer.Serialize(artistsWithImages), // Store as JSON
                // Storing genres as comma-separated list of slugs
                FavoriteGenres = string.Join(",", genres),
            };
        }
        else
        {
            user.MusicProfile.FavoriteSongs = string.Join(", ", songs);
            user.MusicProfile.FavoriteArtists = JsonSerializer.Serialize(artistsWithImages); // Store as JSON
            user.MusicProfile.FavoriteGenres = string.Join(",", genres);
        }

        // 5. Save changes
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<List<SpotifySongDto>> GetArtistTopTracksAsync(string artistName, int limit = 10)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null) throw new Exception("Could not authenticate with Spotify");

        try
        {
            // First, search for the artist
            var searchRequest = new SearchRequest(SearchRequest.Types.Artist, artistName) { Limit = 1 };
            var searchResponse = await _spotify.Search.Item(searchRequest);

            var artist = searchResponse.Artists.Items?.FirstOrDefault();
            if (artist == null)
            {
                Console.WriteLine($"Artist '{artistName}' not found");
                return new List<SpotifySongDto>();
            }

            // Get the artist's top tracks
            var topTracks = await _spotify.Artists.GetTopTracks(artist.Id, new ArtistsTopTracksRequest("US"));

            return topTracks.Tracks
                .Take(limit)
                .Select(track => new SpotifySongDto
                {
                    Title = track.Name,
                    Artist = string.Join(", ", track.Artists.Select(a => a.Name))
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting tracks for artist '{artistName}': {ex.Message}");
            return new List<SpotifySongDto>();
        }
    }

    /// <summary>
    /// Gets genres from a list of artist names.
    /// </summary>
    public async Task<List<string>> GetGenresFromArtistsAsync(List<string> artistNames)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null) throw new Exception("Could not authenticate with Spotify");

        var allGenres = new List<string>();

        foreach (var artistName in artistNames)
        {
            try
            {
                // Search for the artist
                var searchRequest = new SearchRequest(SearchRequest.Types.Artist, artistName) { Limit = 1 };
                var searchResponse = await _spotify.Search.Item(searchRequest);

                var artist = searchResponse.Artists.Items?.FirstOrDefault();
                if (artist == null) continue;

                // Get full artist details for genres
                var fullArtist = await _spotify.Artists.Get(artist.Id);

                if (fullArtist.Genres != null && fullArtist.Genres.Count > 0)
                {
                    allGenres.AddRange(fullArtist.Genres);
                }

                // Small delay to avoid rate limiting
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting genres for '{artistName}': {ex.Message}");
            }
        }

        // Return unique genres, sorted by frequency
        return allGenres
            .GroupBy(g => g)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(20)
            .ToList();
    }

    // DTO for song response
    public class SpotifySongDto
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
    }
}