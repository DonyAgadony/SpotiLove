using SpotifyAPI.Web;
using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public class SpotifyService
{
    private SpotifyClient? _spotify;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;

    private string? _refreshToken;

    public SpotifyService(string clientId, string clientSecret, string redirectUri)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
    }

    // ---------------------------
    // LOGIN
    // ---------------------------
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
                Scopes.UserTopRead
            }
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
        if (_refreshToken == null) throw new Exception("No refresh token available");

        var oauth = new OAuthClient();
        var response = await oauth.RequestToken(
            new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, _refreshToken)
        );

        _spotify = new SpotifyClient(response.AccessToken);
        _refreshToken = response.RefreshToken ?? _refreshToken;
    }

    // ---------------------------
    // SPOTIFY DATA
    // ---------------------------
    public async Task<PrivateUser> GetUserProfileAsync()
    {
        if (_spotify == null) throw new Exception("User not connected");
        return await _spotify.UserProfile.Current();
    }

    public async Task<List<string>> GetUserTopArtistsAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topArtists = await _spotify.Personalization.GetTopArtists(new PersonalizationTopRequest
        {
            Limit = limit,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm
        });

        return topArtists.Items?.Select(a => a.Name).ToList() ?? new List<string>();
    }

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

    public async Task<List<string>> GetUserTopGenresAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topArtists = await _spotify.Personalization.GetTopArtists(new PersonalizationTopRequest
        {
            Limit = limit,
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

        return genres.Distinct().Take(limit).ToList();
    }

    // ---------------------------
    // ADDITIONAL HELPER METHODS
    // ---------------------------
    public async Task<List<string>> GetUserTopSongsWithSlugAsync(int limit = 10)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var topTracks = await _spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
        {
            Limit = limit,
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm
        });

        return topTracks.Items?.Select(t => CreateSlug($"{t.Name}-{t.Artists.FirstOrDefault()?.Name}")).ToList() ?? new List<string>();
    }

    private string CreateSlug(string input)
    {
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

    // ---------------------------
    // SAVE TO DB
    // ---------------------------
    public async Task<User> UpdateUserProfileInDb(AppDbContext db, int userId)
    {
        if (_spotify == null) throw new Exception("User not connected");

        var profile = await GetUserProfileAsync();
        var songs = await GetUserTopSongsAsync();
        var artists = await GetUserTopArtistsAsync();
        var topSongsSlug = await GetUserTopSongsWithSlugAsync();

        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            throw new Exception("User not found");

        // Update EF User
        user.Name = string.IsNullOrEmpty(user.Name) ? profile.DisplayName ?? user.Name : user.Name;

        if (user.MusicProfile == null)
        {
            user.MusicProfile = new MusicProfile
            {
                FavoriteSongs = string.Join(", ", songs),
                FavoriteArtists = string.Join(", ", artists),
                FavoriteGenres = string.Join(",", topSongsSlug)
            };
        }
        else
        {
            user.MusicProfile.FavoriteSongs = string.Join(", ", songs);
            user.MusicProfile.FavoriteArtists = string.Join(", ", artists);
            user.MusicProfile.FavoriteSongs = string.Join(",", topSongsSlug);
        }

        await db.SaveChangesAsync();
        return user;
    }
}