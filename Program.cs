using Microsoft.EntityFrameworkCore;
using Spotilove;
using DotNetEnv;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;
using static Spotilove.AppDbContext;
using Microsoft.AspNetCore.Mvc;

DotNetEnv.Env.Load(); // load .env file

var builder = WebApplication.CreateBuilder(args);

// ===========================================================
// 🧩 DATABASE CONFIGURATION (supports SQLite + PostgreSQL)
// ===========================================================
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetValue<string>("ConnectionStrings:Sqlite")
    ?? "Data Source=spotilove.db";

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
    {
        var databaseUri = new Uri(connectionString);
        var userInfo = databaseUri.UserInfo.Split(':', 2);

        var connStr =
            $"Host={databaseUri.Host};" +
            $"Port={databaseUri.Port};" +
            $"Database={databaseUri.LocalPath.TrimStart('/')};" +
            $"Username={userInfo[0]};" +
            $"Password={userInfo[1]};" +
            $"SSL Mode=Require;" +
            $"Trust Server Certificate=true";

        opt.UseNpgsql(connStr)
           .UseSnakeCaseNamingConvention();

        Console.WriteLine("Using PostgreSQL database");
    }
    else
    {
        opt.UseSqlite(connectionString);
        Console.WriteLine("Using SQLite database (local dev)");
    }
});

// ===========================================================
// 🚀 API & SERVICES CONFIGURATION
// ===========================================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<SwipeService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddSingleton(provider => new SpotifyService(
    Environment.GetEnvironmentVariable("SpotifyClientKey") ?? throw new Exception("ClientKey missing"),
    Environment.GetEnvironmentVariable("SpotifyClientSecret") ?? throw new Exception("ClientSecret missing"),
    Environment.GetEnvironmentVariable("RedirectURI") ?? throw new Exception("RedirectURI missing")
));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors("AllowAll");

// ===========================================================
// 🧱 DATABASE MIGRATION + SEEDING
// ===========================================================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await db.Database.MigrateAsync();

    if (!await db.Users.AnyAsync())
    {
        await SeedDatabaseAsync(db);
        Console.WriteLine("✅ Seeded database with 100 users");
    }
    else
    {
        Console.WriteLine("✅ Database already seeded. Skipping initialization.");
    }
}

// ===========================================================
// 🧑‍💻 DEVELOPMENT TOOLS
// ===========================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ===========================================================
// 🌐 SERVER CONFIGURATION
// ===========================================================
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Urls.Add($"http://0.0.0.0:{port}");

// ===========================================================
// 🌟 API ENDPOINTS
// ===========================================================

// Health check
app.MapGet("/", () => Results.Ok(new
{
    message = "Spotilove API is running!",
    timestamp = DateTime.UtcNow,
    endpoints = new
    {
        users = "/users?userId={id}",
        user_images = "/users/{id}/images",
        swipe_discover = "/swipe/discover/{userId}",
        swipe_action = "/swipe",
        swipe_like = "/swipe/{fromUserId}/like/{toUserId}",
        swipe_pass = "/swipe/{fromUserId}/pass/{toUserId}",
        matches = "/matches/{userId}",
        swipe_stats = "/swipe/stats/{userId}",
        swagger = "/swagger"
    }
}));
// Get popular artists for selection
app.MapGet("/spotify/popular-artists", async (SpotifyService spotifyService, int limit = 20) =>
{
    try
    {
        var artists = await spotifyService.GetPopularArtistsAsync(limit);
        return Results.Ok(artists);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error fetching popular artists: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to fetch popular artists");
    }
})
.WithName("GetPopularArtists")
.WithSummary("Get popular artists from Spotify's Top Hits playlist");

// Search for artists
app.MapGet("/spotify/search-artists", async (SpotifyService spotifyService, string query, int limit = 20) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest("Query parameter is required");

        var artists = await spotifyService.SearchArtistsAsync(query, limit);
        return Results.Ok(artists);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error searching artists: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to search artists");
    }
})
.WithName("SearchArtists")
.WithSummary("Search for artists on Spotify");

app.MapGet("/debug/all-users", async (AppDbContext db) =>
{
    try
    {
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .OrderByDescending(u => u.CreatedAt)
            .Take(50) // Get last 20 users
            .ToListAsync();

        Console.WriteLine($"📊 Total users in database: {users.Count}");

        var result = users.Select(u => new
        {
            u.Id,
            u.Name,
            u.Email,
            u.Age,
            u.Gender,
            u.SexualOrientation,
            u.Bio,
            u.CreatedAt,
            HasMusicProfile = u.MusicProfile != null,
            MusicProfile = u.MusicProfile != null ? new
            {
                u.MusicProfile.FavoriteGenres,
                u.MusicProfile.FavoriteArtists,
                u.MusicProfile.FavoriteSongs
            } : null
        }).ToList();

        return Results.Ok(new
        {
            success = true,
            totalUsers = users.Count,
            users = result
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error fetching users: {ex.Message}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to fetch users",
            statusCode: 500
        );
    }
})
.WithName("DebugAllUsers")
.WithSummary("Debug: View all users in database");
// Get top tracks from an artist
app.MapGet("/spotify/artist-top-tracks"!, async (
    SpotifyService spotifyService,
    string artistName,
    int limit = 10) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Results.BadRequest(new { success = false, message = "Artist name is required" });

        Console.WriteLine($"🎵 Fetching tracks for artist: {artistName}");
        var tracks = await spotifyService.GetArtistTopTracksAsync(artistName, limit);

        Console.WriteLine($"✅ Found {tracks.Count} tracks");
        foreach (var track in tracks)
        {
            Console.WriteLine($"   - {track.Title} | Preview: {(track.PreviewUrl != null ? "✓" : "✗")}");
        }

        return Results.Ok(tracks);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error fetching artist tracks: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to fetch artist tracks");
    }
})
.WithName("GetArtistTopTracks")
.WithSummary("Get top tracks from a specific artist with preview URLs");

// Get genres from selected artists
app.MapGet("/spotify/genres-from-artists", async (SpotifyService spotifyService, string artists) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(artists))
            return Results.BadRequest("Artists parameter is required");

        var artistList = artists.Split(',').Select(a => a.Trim()).ToList();
        var genres = await spotifyService.GetGenresFromArtistsAsync(artistList);

        return Results.Ok(genres);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error fetching genres: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to fetch genres");
    }
})
.WithName("GetGenresFromArtists")
.WithSummary("Get genres based on selected artists");

// Update user music profile
app.MapPost("/users/{userId:int}/profile", async (
    AppDbContext db,
    int userId,
    UpdateMusicProfileRequest request) =>
{
    try
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            return Results.NotFound(new { success = false, message = "User not found" });

        if (user.MusicProfile == null)
        {
            user.MusicProfile = new MusicProfile
            {
                UserId = userId,
                FavoriteArtists = request.Artists,
                FavoriteSongs = request.Songs,
                FavoriteGenres = request.Genres
            };
            db.MusicProfiles.Add(user.MusicProfile);
        }
        else
        {
            user.MusicProfile.FavoriteArtists = request.Artists;
            user.MusicProfile.FavoriteSongs = request.Songs;
            user.MusicProfile.FavoriteGenres = request.Genres;
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            success = true,
            message = "Music profile updated successfully",
            user = new
            {
                user.Id,
                user.Name,
                musicProfile = new
                {
                    artists = user.MusicProfile.FavoriteArtists,
                    songs = user.MusicProfile.FavoriteSongs,
                    genres = user.MusicProfile.FavoriteGenres
                }
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error updating music profile: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to update music profile");
    }
})
.WithName("UpdateUserMusicProfile")
.WithSummary("Update user's music profile with artists, songs, and genres");
app.MapGet("/users", async (AppDbContext db, [FromQuery] int? userId, [FromQuery] int? count) =>
{
    try
    {
        // ✅ Validate required userId
        if (userId == null || userId <= 0)
        {
            return Results.BadRequest(new TakeExUsersResponse
            {
                Success = false,
                Count = 0,
                Users = new(),
                Message = "Missing or invalid userId query parameter"
            });
        }

        int currentUserId = userId.Value;
        int requestedCount = Math.Clamp(count ?? 10, 1, 50);

        // 1️⃣ Load current user + profile
        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        System.Console.WriteLine(currentUser?.MusicProfile);

        if (currentUser?.MusicProfile == null)
        {
            return Results.NotFound(new TakeExUsersResponse
            {
                Success = false,
                Count = 0,
                Users = new(),
                Message = "User not found or missing music profile"
            });
        }

        // 2️⃣ Fetch swiped, queued, and total users in parallel
        var swipedTask = db.Likes
            .Where(l => l.FromUserId == currentUserId)
            .AsNoTracking()
            .Select(l => l.ToUserId)
            .ToListAsync();

        var queueTask = db.UserSuggestionQueues
            .Where(q => q.UserId == currentUserId && q.CompatibilityScore >= 50)
            .OrderByDescending(q => q.CompatibilityScore)
            .ThenBy(q => q.QueuePosition)
            .AsNoTracking()
            .ToListAsync();

        var totalUsersTask = db.Users
            .Where(u => u.Id != currentUserId && u.MusicProfile != null)
            .CountAsync();

        await Task.WhenAll(swipedTask, queueTask, totalUsersTask);

        var swipedUserIds = swipedTask.Result.ToHashSet();
        var queueItems = queueTask.Result;
        var totalAvailable = totalUsersTask.Result;

        Console.WriteLine($"📊 User {currentUserId}: {queueItems.Count} queued, {swipedUserIds.Count} swiped, {totalAvailable} total");

        // 3️⃣ Refill queue if needed
        var queuedUserIds = queueItems.Select(q => q.SuggestedUserId).ToHashSet();
        bool needsQueueRefill = queueItems.Count < requestedCount * 2;

        if (needsQueueRefill && totalAvailable > swipedUserIds.Count + queuedUserIds.Count)
        {
            int batchSize = Math.Min(50, requestedCount * 3);

            var candidateIds = await db.Users
                .Where(u => u.Id != currentUserId &&
                            u.MusicProfile != null &&
                            !swipedUserIds.Contains(u.Id) &&
                            !queuedUserIds.Contains(u.Id))
                .AsNoTracking()
                .Select(u => u.Id)
                .Take(batchSize)
                .ToListAsync();

            if (candidateIds.Any())
            {
                Console.WriteLine($"🔄 Batch processing {candidateIds.Count} new candidates...");

                var candidates = await db.Users
                    .Include(u => u.MusicProfile)
                    .Where(u => candidateIds.Contains(u.Id))
                    .AsNoTracking()
                    .ToListAsync();

                var scoredCandidates = candidates
                    .AsParallel()
                    .Select(user => new
                    {
                        UserId = user.Id,
                        Score = CalculateLocalCompatibility(
                            currentUser.MusicProfile,
                            user.MusicProfile!,
                            currentUser, user)
                    })
                    .OrderByDescending(x => x.Score)
                    .ToList();

                int nextPosition = queueItems.Any()
                    ? queueItems.Max(q => q.QueuePosition) + 1
                    : 0;

                var batchInserts = scoredCandidates.Select((scored, index) =>
                    new UserSuggestionQueue
                    {
                        UserId = currentUserId,
                        SuggestedUserId = scored.UserId,
                        QueuePosition = nextPosition + index,
                        CompatibilityScore = scored.Score,
                        CreatedAt = DateTime.UtcNow
                    }).ToList();

                try
                {
                    var existingPairs = await db.UserSuggestionQueues
                        .Where(q => q.UserId == currentUserId &&
                                    candidateIds.Contains(q.SuggestedUserId))
                        .Select(q => q.SuggestedUserId)
                        .ToListAsync();

                    var newInserts = batchInserts
                        .Where(b => !existingPairs.Contains(b.SuggestedUserId))
                        .ToList();

                    if (newInserts.Any())
                    {
                        db.UserSuggestionQueues.AddRange(newInserts);
                        await db.SaveChangesAsync();
                        Console.WriteLine($"✅ Batch inserted {newInserts.Count} queue items (filtered from {batchInserts.Count})");

                        queueItems.AddRange(newInserts);
                        queueItems = queueItems
                            .OrderByDescending(q => q.CompatibilityScore)
                            .Take(requestedCount * 3)
                            .ToList();

                        var idsToUpdate = scoredCandidates
                            .Where(s => s.Score >= 60)
                            .Take(10)
                            .Select(s => s.UserId)
                            .ToList();

                        if (idsToUpdate.Any())
                        {
                            _ = Task.Run(() => UpdateQueueScoresInBackground(
                                currentUserId,
                                idsToUpdate));
                        }
                    }
                    else
                    {
                        Console.WriteLine("⚠️ All candidates already existed in queue, skipping insert.");
                    }
                }
                catch (DbUpdateException ex)
                {
                    Console.WriteLine($"⚠️ Batch insert conflict: {ex.InnerException?.Message}");
                }
            }
        }

        // 4️⃣ Return top suggestions
        var topSuggestionIds = queueItems
            .Take(requestedCount)
            .Select(q => q.SuggestedUserId)
            .ToList();

        if (!topSuggestionIds.Any())
        {
            return Results.Ok(new TakeExUsersResponse
            {
                Success = true,
                Count = 0,
                Users = new(),
                Message = "No more users available"
            });
        }

        // 5️⃣ Fetch user details and FILTER users without MusicProfile
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .Where(u => topSuggestionIds.Contains(u.Id))
            .AsNoTracking()
            .ToListAsync();

        // ✅ FIX: Filter out users without MusicProfile before mapping
        var validUsers = users.Where(u => u.MusicProfile != null).ToList();

        if (validUsers.Count < users.Count)
        {
            Console.WriteLine($"⚠️ Filtered out {users.Count - validUsers.Count} users without MusicProfile");
        }

        var userDict = validUsers.ToDictionary(u => u.Id);
        var orderedUsers = topSuggestionIds
            .Where(id => userDict.ContainsKey(id))
            .Select(id => userDict[id])
            .ToList();

        var suggestions = orderedUsers.Select(ToUserDto).ToList();

        return Results.Ok(new TakeExUsersResponse
        {
            Success = true,
            Count = suggestions.Count,
            Users = suggestions,
            Message = $"Returned {suggestions.Count} users ({queueItems.Count} in queue)"
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error in /users: {ex.Message}\n{ex.StackTrace}");
        return Results.Ok(new TakeExUsersResponse
        {
            Success = false,
            Count = 0,
            Users = new(),
            Message = $"Error: {ex.Message}"
        });
    }
})
.WithName("GetUsersForSwipe")
.WithSummary("Get personalized user suggestions with smart caching")
.WithDescription("Requires ?userId={id} and optional &count={1–50} for results.");
// ---- Music Profile ----
app.MapPost("/users/{id:int}/music-profile", async (AppDbContext db, int id, MusicProfileDto dto) =>
{
    var user = await db.Users
        .Include(u => u.MusicProfile)
        .FirstOrDefaultAsync(u => u.Id == id);

    if (user == null)
        return Results.NotFound(new { success = false, message = "User not found" });

    if (user.MusicProfile == null)
    {
        // create new profile
        user.MusicProfile = new MusicProfile
        {
            UserId = id,
            FavoriteGenres = dto.FavoriteGenres,
            FavoriteArtists = dto.FavoriteArtists,
            FavoriteSongs = dto.FavoriteSongs
        };
        db.MusicProfiles.Add(user.MusicProfile);
    }
    else
    {
        // update existing profile
        user.MusicProfile.FavoriteGenres = dto.FavoriteGenres;
        user.MusicProfile.FavoriteArtists = dto.FavoriteArtists;
        user.MusicProfile.FavoriteSongs = dto.FavoriteSongs;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        success = true,
        message = "Music profile added/updated successfully",
        profile = dto
    });
});


// ===== HELPER FUNCTIONS =====
static UserDto ToUserDto(User user) => new()
{
    Id = user.Id,
    Name = user.Name,
    Email = user.Email,
    Age = user.Age,
    Location = user.Location,
    Bio = user.Bio,
    SexualOrientation = user.SexualOrientation,
    MusicProfile = user.MusicProfile != null ? new MusicProfileDto
    {
        FavoriteGenres = user.MusicProfile.FavoriteGenres,
        FavoriteArtists = user.MusicProfile.FavoriteArtists,
        FavoriteSongs = user.MusicProfile.FavoriteSongs
    } : null,
    Images = user.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
};

static double CalculateLocalCompatibility(MusicProfile p1, MusicProfile p2, User u1, User u2)
{
    // Existing music scoring
    double genreScore = JaccardSimilarity(p1.FavoriteGenres, p2.FavoriteGenres);
    double artistScore = JaccardSimilarity(p1.FavoriteArtists, p2.FavoriteArtists);
    double songScore = JaccardSimilarity(p1.FavoriteSongs, p2.FavoriteSongs);
    double musicScore = genreScore * 30 + artistScore * 40 + songScore * 30;

    // New: LGBTQ+ preference scoring (0-100, weighted at 20% of total)
    double preferenceScore = CalculatePreferenceCompatibility(u1, u2);

    // Combine: Music (80%) + Preferences (20%)
    return Math.Round((musicScore * 0.8) + (preferenceScore * 0.2));
}
static bool IsAttractedTo(User attractor, string attractedToGender)
{
    // Attractor finds the gender attractive if their orientation matches the gender,
    // or if their orientation is "Both".
    return attractor.SexualOrientation == attractedToGender || attractor.SexualOrientation == "Both";
}

static double CalculatePreferenceCompatibility(User u1, User u2)
{
    // 1. Check if u1 is attracted to u2's gender
    bool u1_Attracted_To_u2 = IsAttractedTo(u1, u2.Gender);

    // 2. Check if u2 is attracted to u1's gender
    bool u2_Attracted_To_u1 = IsAttractedTo(u2, u1.Gender);

    // Compatibility is 100.0 only if attraction is mutual (both ways)
    if (u1_Attracted_To_u2 && u2_Attracted_To_u1)
    {
        return 100.0;
    }

    return 0.0;
}

static double JaccardSimilarity(string a, string b)
{
    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

    var setA = a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
    var setB = b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();

    var intersection = setA.Intersect(setB).Count();
    var union = setA.Union(setB).Count();

    return union == 0 ? 0 : (double)intersection / union * 100;
}

static async Task UpdateQueueScoresInBackground(int userId, List<int> suggestedUserIds)
{
    try
    {
        Console.WriteLine($"Starting background Gemini updates for {suggestedUserIds.Count} users...");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        var cs = Environment.GetEnvironmentVariable("DATABASE_URL")
                 ?? "Data Source=spotilove.db";

        if (cs.StartsWith("postgres://") || cs.StartsWith("postgresql://"))
        {
            optionsBuilder.UseNpgsql(BuildNpgsqlConnectionString(cs));
        }
        else
        {
            optionsBuilder.UseSqlite(cs);
        }

        using var db = new AppDbContext(optionsBuilder.Options);

        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (currentUser?.MusicProfile == null)
        {
            Console.WriteLine("Current user not found in background task");
            return;
        }

        int successCount = 0;
        int failCount = 0;

        foreach (var suggestedId in suggestedUserIds)
        {
            try
            {
                var suggestedUser = await db.Users
                    .Include(u => u.MusicProfile)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == suggestedId);

                if (suggestedUser?.MusicProfile == null)
                {
                    Console.WriteLine($"Suggested user {suggestedId} not found");
                    continue;
                }

                var geminiScore = await GeminiService.CalculatePercentage(
                    currentUser.MusicProfile,
                    suggestedUser.MusicProfile);

                if (geminiScore.HasValue)
                {
                    var queueItem = await db.UserSuggestionQueues
                        .FirstOrDefaultAsync(q =>
                            q.UserId == userId &&
                            q.SuggestedUserId == suggestedId);

                    if (queueItem != null)
                    {
                        queueItem.CompatibilityScore = geminiScore.Value;
                        await db.SaveChangesAsync();
                        successCount++;
                        Console.WriteLine($"Gemini score for user {suggestedId}: {geminiScore}%");
                    }
                }
                else
                {
                    failCount++;
                    Console.WriteLine($"Gemini returned null for user {suggestedId}");
                }

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                failCount++;
                Console.WriteLine($"Gemini failed for user {suggestedId}: {ex.Message}");
            }
        }

        Console.WriteLine($"Background update complete: {successCount} success, {failCount} failed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Background update error: {ex.Message}\n{ex.StackTrace}");
    }
}

static string BuildNpgsqlConnectionString(string databaseUrl)
{
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);

    return new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port,
        Username = userInfo[0],
        Password = userInfo[1],
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = Npgsql.SslMode.Require,
        TrustServerCertificate = true
    }.ConnectionString;
}
app.MapGet("/debug/user/{userId:int}", async (AppDbContext db, int userId) =>
        {
            try
            {
                var user = await db.Users
                    .Include(u => u.MusicProfile)
                    .Include(u => u.Images)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    return Results.NotFound(new
                    {
                        success = false,
                        message = $"User {userId} not found"
                    });
                }

                return Results.Ok(new
                {
                    success = true,
                    userId = user.Id,
                    name = user.Name,
                    email = user.Email,
                    hasMusicProfile = user.MusicProfile != null,
                    musicProfile = user.MusicProfile != null ? new
                    {
                        id = user.MusicProfile.Id,
                        genres = user.MusicProfile.FavoriteGenres,
                        artists = user.MusicProfile.FavoriteArtists,
                        songs = user.MusicProfile.FavoriteSongs,
                        isEmpty = string.IsNullOrWhiteSpace(user.MusicProfile.FavoriteGenres) &&
                                 string.IsNullOrWhiteSpace(user.MusicProfile.FavoriteArtists) &&
                                 string.IsNullOrWhiteSpace(user.MusicProfile.FavoriteSongs)
                    } : null,
                    imageCount = user.Images.Count
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "Debug endpoint failed",
                    statusCode: 500
                );
            }
        })
        .WithName("DebugUserProfile")
        .WithSummary("Debug: Check if a user has a music profile and view its contents");

// ---- Auto-seed endpoint (can be called manually) ----
app.MapPost("/seed-database", async (AppDbContext db) =>
{
    try
    {
        await SeedDatabaseAsync(db);
        return Results.Ok(new { success = true, message = "Database seeded successfully" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "Seeding failed");
    }
});

// 1. Login trigger
app.MapGet("/login", (SpotifyService spotify) =>
{
    var loginUrl = spotify.GetLoginUrl();
    return Results.Redirect(loginUrl);
})
.WithName("SpotifyAuth")
.WithSummary("Redirects to Spotify OAuth (handles both login and signup)");

// SINGLE Spotify OAuth Callback - handles both login and signup
app.MapGet("/callback", async (
    HttpRequest req,
    SpotifyService spotify,
    AppDbContext db,
    IPasswordHasher<User> hasher) =>
{
    try
    {
        var code = req.Query["code"].ToString();
        var error = req.Query["error"].ToString();

        // Handle user declining authorization
        if (!string.IsNullOrEmpty(error))
        {
            var errorRedirect = "spotilove://auth/error?message=Authorization declined";
            return Results.Redirect(errorRedirect);
        }

        if (string.IsNullOrEmpty(code))
        {
            var errorRedirect = "spotilove://auth/error?message=Missing authorization code";
            return Results.Redirect(errorRedirect);
        }

        Console.WriteLine($"🔐 Spotify callback received with code");

        // Connect to Spotify and get access token
        await spotify.ConnectUserAsync(code);
        Console.WriteLine("✅ Connected to Spotify API");

        // Fetch Spotify profile
        var spotifyProfile = await spotify.GetUserProfileAsync();

        if (spotifyProfile == null || string.IsNullOrEmpty(spotifyProfile.Email))
        {
            var errorRedirect = "spotilove://auth/error?message=Unable to fetch email from Spotify";
            return Results.Redirect(errorRedirect);
        }

        Console.WriteLine($"📧 Spotify email: {spotifyProfile.Email}");

        // Check if user exists
        var existingUser = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Email == spotifyProfile.Email);

        User user;
        bool isNewUser = false;

        if (existingUser == null)
        {
            // CREATE NEW USER (Sign Up flow)
            isNewUser = true;

            var randomPassword = Guid.NewGuid().ToString();
            var hashedPassword = hasher.HashPassword(null!, randomPassword);

            user = new User
            {
                Name = spotifyProfile.DisplayName ?? spotifyProfile.Id,
                Email = spotifyProfile.Email,
                PasswordHash = hashedPassword,
                Age = 18,
                Gender = "Prefer not to say",
                CreatedAt = DateTime.UtcNow,
                MusicProfile = new MusicProfile
                {
                    FavoriteGenres = "",
                    FavoriteArtists = "",
                    FavoriteSongs = ""
                }
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            Console.WriteLine($"✅ New user created: {user.Email} (ID: {user.Id})");
        }
        else
        {
            // EXISTING USER (Login flow)
            user = existingUser;
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            Console.WriteLine($"✅ Existing user logged in: {user.Email} (ID: {user.Id})");
        }

        // Fetch and update music profile from Spotify (async, don't block redirect)
        _ = Task.Run(async () =>
        {
            try
            {
                var topSongs = await spotify.GetUserTopSongsAsync(10);
                var topArtists = await spotify.GetUserTopArtistsWithImagesAsync(10);
                var topGenres = await spotify.GetUserTopGenresAsync(20);

                // Create a new DB context for this background task
                var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
                var connectionString = db.Database.GetConnectionString();
                using var bgDb = new AppDbContext(optionsBuilder.Options);

                var bgUser = await bgDb.Users
                    .Include(u => u.MusicProfile)
                    .FirstOrDefaultAsync(u => u.Id == user.Id);

                if (bgUser != null)
                {
                    if (bgUser.MusicProfile == null)
                    {
                        bgUser.MusicProfile = new MusicProfile
                        {
                            UserId = bgUser.Id,
                            FavoriteGenres = string.Join(", ", topGenres),
                            FavoriteArtists = string.Join(", ", topArtists),
                            FavoriteSongs = string.Join(", ", topSongs)
                        };
                    }
                    else
                    {
                        bgUser.MusicProfile.FavoriteGenres = string.Join(", ", topGenres);
                        bgUser.MusicProfile.FavoriteArtists = string.Join(", ", topArtists);
                        bgUser.MusicProfile.FavoriteSongs = string.Join(", ", topSongs);
                    }

                    await bgDb.SaveChangesAsync();

                    Console.WriteLine($"🎵 Music profile updated for user {bgUser.Id}:");
                    Console.WriteLine($"   Genres: {string.Join(", ", topGenres.Take(3))}...");
                    Console.WriteLine($"   Artists: {string.Join(", ", topArtists.Take(3))}...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Background music sync failed: {ex.Message}");
            }
        });

        // Generate auth token
        var token = Guid.NewGuid().ToString();

        // Build deep link URL to redirect back to the app
        var deepLinkUrl = $"spotilove://auth/success?token={Uri.EscapeDataString(token)}&userId={user.Id}&isNewUser={isNewUser}&name={Uri.EscapeDataString(user.Name ?? "User")}";

        Console.WriteLine($"🔗 Redirecting to: {deepLinkUrl}");

        // Return an HTML page that auto-redirects
        var html = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>SpotiLove - Success</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
            background: linear-gradient(135deg, #1db954 0%, #191414 100%);
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            color: white;
        }}
        .container {{
            text-align: center;
            padding: 40px;
            background: rgba(0,0,0,0.5);
            border-radius: 20px;
            max-width: 400px;
        }}
        .spinner {{
            border: 4px solid rgba(255,255,255,0.3);
            border-top: 4px solid #1db954;
            border-radius: 50%;
            width: 50px;
            height: 50px;
            animation: spin 1s linear infinite;
            margin: 0 auto 20px;
        }}
        @keyframes spin {{
            0% {{ transform: rotate(0deg); }}
            100% {{ transform: rotate(360deg); }}
        }}
        h1 {{ margin: 0 0 10px; font-size: 28px; }}
        p {{ margin: 10px 0; opacity: 0.9; }}
        .manual-link {{
            margin-top: 20px;
            padding: 15px 30px;
            background: #1db954;
            color: white;
            text-decoration: none;
            border-radius: 25px;
            display: inline-block;
            font-weight: bold;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='spinner'></div>
        <h1>✨ {(isNewUser ? "Welcome to SpotiLove!" : "Welcome Back!")}</h1>
        <p>Redirecting you back to the app...</p>
        <p style='font-size: 14px; opacity: 0.7;'>If you're not redirected automatically, click below:</p>
        <a href='{deepLinkUrl}' class='manual-link'>Open SpotiLove</a>
    </div>
    <script>
        setTimeout(() => {{
            window.location.href = '{deepLinkUrl}';
        }}, 1500);
        
        setTimeout(() => {{
            window.close();
        }}, 3000);
    </script>
</body>
</html>";

        return Results.Content(html, "text/html");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Spotify callback error: {ex.Message}\n{ex.StackTrace}");

        var errorDeepLink = $"spotilove://auth/error?message={Uri.EscapeDataString(ex.Message)}";

        var errorHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <title>SpotiLove - Error</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
            background: #191414;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            color: white;
        }}
        .container {{
            text-align: center;
            padding: 40px;
            background: rgba(255,0,0,0.1);
            border-radius: 20px;
            max-width: 400px;
        }}
        h1 {{ color: #ff4444; }}
        a {{ color: #1db954; text-decoration: none; font-weight: bold; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>❌ Authentication Failed</h1>
        <p>{ex.Message}</p>
        <p><a href='{errorDeepLink}'>Return to App</a></p>
    </div>
</body>
</html>";

        return Results.Content(errorHtml, "text/html");
    }
})
.WithName("SpotifyCallback")
.WithSummary("Handles Spotify OAuth callback for both login and signup");
//Take 10 Example Users from DB
// ---- NEW: Take 10 Example Users Endpoint ----
// ---- NEW: Take Random Users & Compare Music Taste ----
app.MapGet("/takeExUsers", async (AppDbContext db, int count) =>
{
    try
    {
        Console.WriteLine($"🎯 Taking {count} random users from DB...");

        // Fetch all users first (or a large batch if your DB grows big)
        var allUsers = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .ToListAsync();

        if (allUsers.Count == 0)
        {
            Console.WriteLine("⚠️ No users found in DB");
            return Results.NotFound(new { success = false, message = "No users found in database" });
        }

        // Shuffle in memory
        var random = new Random();
        var users = allUsers.OrderBy(_ => random.Next()).Take(count).ToList();

        Console.WriteLine($"✅ Retrieved {users.Count} random users");

        return Results.Ok(new
        {
            success = true,
            count = users.Count,
            users = users.Select(u => new
            {
                u.Id,
                u.Name,
                u.Email,
                u.Age,
                u.Location,
                u.Bio,
                MusicProfile = new
                {
                    u.MusicProfile?.FavoriteSongs,
                    u.MusicProfile?.FavoriteArtists,
                    u.MusicProfile?.FavoriteGenres
                },
                Images = u.Images.Select(img => img.ImageUrl).ToList()
            })
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to take users: {ex.Message}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to take users",
            statusCode: 500
        );
    }
})
.WithName("TakeUsersFromDB")
.WithSummary("Take N random users from the database")
.WithDescription("Fetches N random users (with music profile and images) from the database for testing");

//dislike Operation
// app.MapGet("/dislikeUser", (AppDbContext db) => );
// ---- NEW: Add Example Users Endpoint ----
// ---- NEW: Add 100 Example Users Endpoint ----
app.MapPost("/test/add-users", async (AppDbContext db) =>
{
    try
    {
        Console.WriteLine("👥 Adding 100 example users to database...");

        await SeedDatabaseAsync(db);

        var savedUsers = await db.Users.Include(u => u.MusicProfile).Include(u => u.Images).ToListAsync();

        Console.WriteLine($"✅ Successfully added {savedUsers.Count} example users to database");

        return Results.Ok(new
        {
            success = true,
            message = $"Successfully added {savedUsers.Count} example users to database",
            count = savedUsers.Count
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error adding users: {ex.Message}");
        return Results.Problem(detail: ex.Message, title: "Failed to add users", statusCode: 500);
    }
})
.WithName("AddExampleUsers")
.WithSummary("Add 100 example users to the database")
.WithDescription("Adds 100 example users with random profiles for testing");

app.MapPost("/auth/register", async (
    RegisterRequest request,
    AppDbContext db,
    IPasswordHasher<User> hasher) =>
{
    try
    {
        Console.WriteLine($"📝 Registration attempt for email: {request.Email}");

        // Check if email already exists
        if (await db.Users.AnyAsync(u => u.Email == request.Email))
        {
            Console.WriteLine($"❌ Email already exists: {request.Email}");
            return Results.BadRequest(new { success = false, message = "Email already exists" });
        }

        // Hash password
        var hashedPassword = hasher.HashPassword(null!, request.Password);
        Console.WriteLine("✅ Password hashed successfully");

        // Create user with ALL required fields
        var user = new User
        {
            Name = request.Name,
            Email = request.Email,
            PasswordHash = hashedPassword,
            Age = request.Age,
            Gender = request.Gender,
            SexualOrientation = request.SexualOrientation, // Include this
            Bio = request.Bio, // Include bio from request
            CreatedAt = DateTime.UtcNow,
            Location = null, // Can be set later
            LastLoginAt = null
        };

        Console.WriteLine($"✅ User object created: {user.Name}");

        // Add user to database
        db.Users.Add(user);

        // Save changes and get the user ID
        var saveResult = await db.SaveChangesAsync();
        Console.WriteLine($"✅ SaveChanges returned: {saveResult} changes");
        Console.WriteLine($"✅ User ID assigned: {user.Id}");

        if (user.Id <= 0)
        {
            Console.WriteLine("❌ User ID was not assigned properly!");
            return Results.Problem("Failed to create user - ID not assigned");
        }

        // Generate token
        var token = Guid.NewGuid().ToString();
        Console.WriteLine($"✅ Token generated: {token}");

        // Verify user was saved by querying it back
        var savedUser = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
        if (savedUser == null)
        {
            Console.WriteLine($"❌ User {user.Id} was not found after save!");
            return Results.Problem("User creation failed - could not verify save");
        }

        Console.WriteLine($"✅ User verified in database: {savedUser.Email}");

        return Results.Ok(new
        {
            success = true,
            message = "User registered successfully",
            token,
            user = new
            {
                user.Id,
                user.Name,
                user.Email,
                user.Age,
                user.Gender,
                user.SexualOrientation,
                user.Bio
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Registration error: {ex.Message}");
        Console.WriteLine($"❌ Stack trace: {ex.StackTrace}");

        if (ex.InnerException != null)
        {
            Console.WriteLine($"❌ Inner exception: {ex.InnerException.Message}");
        }

        return Results.Problem(
            detail: ex.Message,
            title: "Registration failed",
            statusCode: 500
        );
    }
});

app.MapPost("/auth/login", async (
    LoginRequestFromApp request,
    AppDbContext db,
    IPasswordHasher<User> hasher) =>
{
    // Find user by email
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (user == null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Invalid email or password"
        });
    }

    // Verify password
    var result = hasher.VerifyHashedPassword(user, user.PasswordHash ?? "", request.Password); if (result == PasswordVerificationResult.Failed)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Invalid email or password"
        });
    }

    // Generate a fake token for now (replace with JWT later)
    var token = Guid.NewGuid().ToString();

    // Optional: handle "RememberMe" if you want longer token lifetime
    // (right now, it's just informational)

    return Results.Ok(new
    {
        success = true,
        message = "Login successful",
        token,
        user = new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Age,
            user.Gender
        }
    });
});

// Add this endpoint to Program.cs to fix users without music profiles:

app.MapPost("/fix-missing-profiles", async (AppDbContext db) =>
{
    try
    {
        Console.WriteLine("🔧 Checking for users without music profiles...");

        // Find all users without music profiles
        var usersWithoutProfiles = await db.Users
            .Include(u => u.MusicProfile)
            .Where(u => u.MusicProfile == null)
            .ToListAsync();

        if (usersWithoutProfiles.Count == 0)
        {
            return Results.Ok(new
            {
                success = true,
                message = "All users already have music profiles",
            });
        }

        Console.WriteLine($"⚠️ Found {usersWithoutProfiles.Count} users without music profiles");

        // Create default music profiles
        var random = new Random();
        var sampleGenres = new[] { "Pop", "Rock", "Hip Hop", "Jazz", "Electronic", "Classical", "Metal", "R&B", "Indie", "Latin" };
        var sampleArtists = new[] { "Taylor Swift", "Drake", "Arctic Monkeys", "Beyoncé", "Eminem", "Daft Punk", "Bad Bunny", "Mozart", "The Weeknd", "Metallica" };
        var sampleSongs = new[] { "Anti-Hero", "Blinding Lights", "Do I Wanna Know", "One More Time", "La Vida Es Un Carnaval", "HUMBLE.", "Enter Sandman", "Creep", "Kind of Blue", "Everlong" };

        foreach (var user in usersWithoutProfiles)
        {
            var profile = new MusicProfile
            {
                UserId = user.Id,
                FavoriteSongs = string.Join(", ", sampleSongs.OrderBy(_ => random.Next()).Take(3)),
                FavoriteArtists = string.Join(", ", sampleArtists.OrderBy(_ => random.Next()).Take(3)),
                FavoriteGenres = string.Join(", ", sampleGenres.OrderBy(_ => random.Next()).Take(3))
            };

            db.MusicProfiles.Add(profile);
            Console.WriteLine($"   ✅ Created profile for user {user.Id} ({user.Name})");
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            success = true,
            message = $"Created music profiles for {usersWithoutProfiles.Count} users",
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error fixing profiles: {ex.Message}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to fix missing profiles",
            statusCode: 500
        );
    }
})
.WithName("FixMissingProfiles")
.WithSummary("Create music profiles for users that don't have them");

// ---- User Management Endpoints ----
app.MapPost("/users", Endpoints.CreateUser);
app.MapGet("/users/{id:int}", Endpoints.GetUser);
app.MapPut("/users/{id:int}/profile", Endpoints.UpdateProfile);
app.MapGet("/users:search", Endpoints.SearchUsers);

// ---- User Images Endpoints ----
app.MapPost("/users/{id:int}/images", Endpoints.AddUserImage);
app.MapGet("/users/{id:int}/images", Endpoints.GetUserImages);

// ---- Swiping Endpoints ----
app.MapGet("/swipe/discover/{userId:int}", SwipeEndpoints.GetPotentialMatches);
app.MapPost("/swipe", SwipeEndpoints.SwipeOnUser);
app.MapPost("/swipe/{fromUserId:int}/like/{toUserId:int}", SwipeEndpoints.LikeUser);
app.MapPost("/swipe/{fromUserId:int}/pass/{toUserId:int}", SwipeEndpoints.PassUser);
app.MapGet("/matches/{userId:int}", SwipeEndpoints.GetUserMatches);
app.MapGet("/swipe/stats/{userId:int}", SwipeEndpoints.GetSwipeStats);


Console.WriteLine("🎯 Spotilove API is starting...");
Console.WriteLine($"🌐 Running on port: {port}");
Console.WriteLine("🤖 Gemini AI compatibility calculation enabled");
Console.WriteLine("⚙️  Make sure to set GeminiAPIKey environment variable for AI features");
Console.WriteLine("🧪 Test endpoints available:");
Console.WriteLine("   - POST /fix-user101");
Console.WriteLine("   - POST /seed-database");
Console.WriteLine($"📖 View API documentation at: http://localhost:{port}/swagger");

app.Run();

// ===========================================================
// HELPER METHOD: Seed example users
// ===========================================================
static async Task SeedDatabaseAsync(AppDbContext db)
{
    var existingCount = await db.Users.CountAsync();
    if (existingCount >= 100)
    {
        Console.WriteLine($"⚠️ Database already has {existingCount} users. Skipping seed.");
        return;
    }

    Console.WriteLine("🌱 Starting database seeding...");

    var random = new Random();
    var sampleGenres = new[] { "Pop", "Rock", "Hip Hop", "Jazz", "Electronic", "Classical", "Metal", "R&B", "Indie", "Latin" };
    var sampleArtists = new[] { "Taylor Swift", "Drake", "Arctic Monkeys", "Beyoncé", "Eminem", "Daft Punk", "Bad Bunny", "Mozart", "The Weeknd", "Metallica" };
    var sampleSongs = new[] { "Anti-Hero", "Blinding Lights", "Do I Wanna Know", "One More Time", "La Vida Es Un Carnaval", "HUMBLE.", "Enter Sandman", "Creep", "Kind of Blue", "Everlong" };
    var sampleLocations = new[] { "New York, NY", "Los Angeles, CA", "Austin, TX", "Seattle, WA", "Miami, FL", "Chicago, IL", "London, UK", "Berlin, DE", "Paris, FR", "Tel Aviv, IL" };

    var users = new List<User>();

    for (int i = 1; i <= 100; i++)
    {
        var name = $"Test User {i}";
        var email = $"user{i}@example.com";
        var age = random.Next(18, 40);
        var location = sampleLocations[random.Next(sampleLocations.Length)];
        var bio = $"I am {name}, I love music and meeting new people!";

        // ✅ Create user with embedded MusicProfile
        var user = new User
        {
            Name = name,
            Email = email,
            PasswordHash = "placeholder_hash",  // Add password hash
            Age = age,
            Gender = "Other",  // Add required gender field
            Location = location,
            Bio = bio,
            CreatedAt = DateTime.UtcNow,
            MusicProfile = new MusicProfile
            {
                FavoriteSongs = string.Join(", ", sampleSongs.OrderBy(_ => random.Next()).Take(3)),
                FavoriteArtists = string.Join(", ", sampleArtists.OrderBy(_ => random.Next()).Take(3)),
                FavoriteGenres = string.Join(", ", sampleGenres.OrderBy(_ => random.Next()).Take(3))
            }
        };

        users.Add(user);
    }

    Console.WriteLine($"📝 Adding {users.Count} users to database...");
    await db.Users.AddRangeAsync(users);
    await db.SaveChangesAsync();
    Console.WriteLine("✅ Users saved");

    // Verify music profiles were created
    var profileCount = await db.MusicProfiles.CountAsync();
    Console.WriteLine($"🎵 Music profiles created: {profileCount}");

    // Add images
    Console.WriteLine("🖼️ Adding user images...");
    var userImages = users.SelectMany(user => new[]
    {
        new UserImage { UserId = user.Id, ImageUrl = $"https://picsum.photos/400/600?random={user.Id}1" },
        new UserImage { UserId = user.Id, ImageUrl = $"https://picsum.photos/400/600?random={user.Id}2" }
    }).ToList();

    await db.UserImages.AddRangeAsync(userImages);
    await db.SaveChangesAsync();
    Console.WriteLine($"✅ Added {userImages.Count} images");

    Console.WriteLine($"✅ Seeding complete: {users.Count} users, {profileCount} music profiles");
}

// DTO for music profile update
public record UpdateMusicProfileRequest(
    string Artists,
    string Songs,
    string Genres
);
