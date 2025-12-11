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
    public SpotifyClient? GetSpotifyClient()
    {
        // The client is already authenticated by the time this is called in the route.
        return _spotify;
    }
    public async Task EnsureClientIsAuthenticatedAsync()
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
    public async Task<List<ArtistInfo>> GetPopularArtistsAsync(int limit = 20)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null)
            throw new Exception("Could not authenticate with Spotify");

        try
        {
            // Get featured playlists (more reliable than hardcoded IDs)
            var featuredPlaylists = await _spotify.Browse.GetFeaturedPlaylists(new FeaturedPlaylistsRequest
            {
                Limit = 1,
                Country = "US"
            });

            if (featuredPlaylists.Playlists?.Items == null || !featuredPlaylists.Playlists.Items.Any())
            {
                Console.WriteLine("No featured playlists found, falling back to search");
                return await GetPopularArtistsFromSearchAsync(limit);
            }

            var firstPlaylist = featuredPlaylists.Playlists.Items.First();
            var playlist = await _spotify.Playlists.Get(firstPlaylist.Id!);

            // Extract unique artist IDs
            var artistIds = playlist.Tracks?.Items?
                .SelectMany(item => item.Track is FullTrack track ? track.Artists : Enumerable.Empty<SimpleArtist>())
                .Where(artist => artist != null && !string.IsNullOrEmpty(artist.Id))
                .Select(artist => artist.Id)
                .Distinct()
                .Take(limit)
                .ToList();

            if (artistIds == null || !artistIds.Any())
            {
                return await GetPopularArtistsFromSearchAsync(limit);
            }

            // Fetch all artist details concurrently
            var artistTasks = artistIds.Select(id => _spotify.Artists.Get(id)).ToList();
            var fullArtists = await Task.WhenAll(artistTasks);

            return fullArtists
                .Where(a => a != null)
                .Select(fullArtist => new ArtistInfo(fullArtist.Name, fullArtist.Images.FirstOrDefault()?.Url))
                .ToList();
        }
        catch (APIException ex)
        {
            Console.WriteLine($"Spotify API Error in GetPopularArtistsAsync: {ex.Response?.StatusCode} - {ex.Message}");
            return await GetPopularArtistsFromSearchAsync(limit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected Error in GetPopularArtistsAsync: {ex.Message}");
            return new List<ArtistInfo>();
        }
    }

    public async Task<List<SpotifySongDto>> GetArtistTopTracksAsync(string artistName, int limit = 10)
    {
        await EnsureClientIsAuthenticatedAsync();
        if (_spotify == null) throw new Exception("Could not authenticate with Spotify");

        try
        {
            Console.WriteLine($"🔍 Searching for artist: {artistName}");

            var searchRequest = new SearchRequest(SearchRequest.Types.Artist, artistName) { Limit = 20 };
            var searchResponse = await _spotify.Search.Item(searchRequest);

            if (searchResponse.Artists.Items == null || !searchResponse.Artists.Items.Any())
            {
                Console.WriteLine($"❌ Artist '{artistName}' not found");
                return new List<SpotifySongDto>();
            }

            // 1. Exact match (best)
            var artist = searchResponse.Artists.Items
                .FirstOrDefault(a =>
                    a.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase));

            // 2. "Almost exact match" (Levenshtein distance <= 2)
            if (artist == null)
            {
                artist = searchResponse.Artists.Items
                    .OrderBy(a => CalculateLevenshteinDistance(a.Name.ToLower(), artistName.ToLower()))
                    .FirstOrDefault();
            }

            // 3. Fallback: Most popular if nothing reasonable found
            if (artist == null)
            {
                artist = searchResponse.Artists.Items
                    .OrderByDescending(a => a.Popularity)
                    .FirstOrDefault();
            }

            if (artist == null)
            {
                Console.WriteLine($"❌ No valid artist match for '{artistName}'");
                return new List<SpotifySongDto>();
            }

            Console.WriteLine($"✅ Final chosen artist: {artist.Name} (ID: {artist.Id})");

            var topTracksResponse = await _spotify.Artists.GetTopTracks(artist.Id, new ArtistsTopTracksRequest("US"));

            if (topTracksResponse.Tracks == null || !topTracksResponse.Tracks.Any())
            {
                return new List<SpotifySongDto>();
            }

            var result = new List<SpotifySongDto>();

            foreach (var track in topTracksResponse.Tracks.Take(limit))
            {
                string? spotifyPreviewUrl = track.PreviewUrl;
                string? deezerPreviewUrl = await GetDeezerPreviewUrlAsync(track.Name, artist.Name);

                result.Add(new SpotifySongDto
                {
                    Title = track.Name,
                    Artist = string.Join(", ", track.Artists.Select(a => a.Name)),
                    PreviewUrl = spotifyPreviewUrl,
                    DeezerPreviewUrl = deezerPreviewUrl,
                    SpotifyUri = track.Uri,
                    SpotifyUrl = track.ExternalUrls.ContainsKey("spotify") ? track.ExternalUrls["spotify"] : null
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            return new List<SpotifySongDto>();
        }
    }
    private int CalculateLevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    /// Searches Deezer for a track and returns its preview URL
    private async Task<string?> GetDeezerPreviewUrlAsync(string trackName, string artistName)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var query = Uri.EscapeDataString($"{trackName} {artistName}");
            var deezerUrl = $"https://api.deezer.com/search?q={query}&limit=5";

            var response = await httpClient.GetAsync(deezerUrl);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
            {
                return null;
            }

            // Find best match
            foreach (var item in data.EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? "";
                var artist = item.GetProperty("artist").GetProperty("name").GetString() ?? "";

                // Check if it's a good match
                if (title.Contains(trackName, StringComparison.OrdinalIgnoreCase) ||
                    trackName.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("preview", out var previewElement))
                    {
                        var previewUrl = previewElement.GetString();
                        if (!string.IsNullOrEmpty(previewUrl))
                        {
                            Console.WriteLine($"   ✅ Found Deezer preview: {trackName}");
                            return previewUrl;
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Deezer search failed for '{trackName}': {ex.Message}");
            return null;
        }
    }

    // Fallback method using search for popular artists
    private async Task<List<ArtistInfo>> GetPopularArtistsFromSearchAsync(int limit)
    {
        try
        {
            // Search for some popular genres/terms to get popular artists
            var popularSearchTerms = new[] { "pop", "hip hop", "rock", "taylor swift", "drake", "bad bunny" };
            var allArtists = new List<ArtistInfo>();
            var seenArtists = new HashSet<string>();

            foreach (var term in popularSearchTerms)
            {
                if (allArtists.Count >= limit) break;

                var searchRequest = new SearchRequest(SearchRequest.Types.Artist, term) { Limit = 10 };
                var searchResponse = await _spotify!.Search.Item(searchRequest);

                var newArtists = searchResponse.Artists.Items?
                    .Where(a => !seenArtists.Contains(a.Name))
                    .Select(a => new ArtistInfo(a.Name, a.Images.FirstOrDefault()?.Url))
                    .Take(limit - allArtists.Count)
                    .ToList() ?? new List<ArtistInfo>();

                foreach (var artist in newArtists)
                {
                    seenArtists.Add(artist.Name);
                    allArtists.Add(artist);
                }

                await Task.Delay(100); // Rate limiting
            }

            return allArtists.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in fallback search: {ex.Message}");
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

    public async Task<User> UpdateUserProfileInDb(AppDbContext db, Guid userId)
    {
        if (_spotify == null)
            throw new Exception("User not connected to Spotify");

        // 1. Fetch data from Spotify
        var profile = await GetUserProfileAsync();                // Gets name + email
        var topSongs = await GetUserTopSongsAsync();              // Musical taste
        var topArtistsWithImages = await GetUserTopArtistsWithImagesAsync();
        var topGenres = await GetUserTopGenresAsync();

        // 2. Load DB user
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            throw new Exception($"User with ID {userId} not found.");

        // 3. Force name and email update from Spotify
        user.Name = profile.DisplayName ?? user.Name ?? "Unknown User";
        user.Email = profile.Email ?? user.Email;

        // 4. Ensure music profile exists
        if (user.MusicProfile == null)
        {
            user.MusicProfile = new MusicProfile();
        }

        // 5. Update musical taste fields
        user.MusicProfile.FavoriteSongs = topSongs.ToList();
        user.MusicProfile.FavoriteArtists = topArtistsWithImages.Select(a => a.Name).ToList();
        user.MusicProfile.FavoriteGenres = topGenres.ToList();

        // 6. Save changes
        await db.SaveChangesAsync();
        return user;
    }

    /// Gets genres from a list of artist names.
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
        public string? PreviewUrl { get; set; }
        public string? SpotifyUri { get; set; }
        public string? SpotifyUrl { get; set; }
        public string? DeezerPreviewUrl { get; set; }

    }
}