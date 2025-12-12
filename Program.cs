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
        Console.WriteLine("Using SQLite database");
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
app.MapPost("/users/{userId:guid}/profile", async (
    AppDbContext db,
    Guid userId,
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
                FavoriteArtists = request.Artists.Split(',').Select(a => a.Trim()).ToList(),
                FavoriteSongs = request.Songs.Split(',').Select(s => s.Trim()).ToList(),
                FavoriteGenres = request.Genres.Split(',').Select(g => g.Trim()).ToList()
            };
            db.MusicProfiles.Add(user.MusicProfile);
        }
        else
        {
            user.MusicProfile.FavoriteArtists = request.Artists.Split(',').Select(a => a.Trim()).ToList();
            user.MusicProfile.FavoriteSongs = request.Songs.Split(',').Select(s => s.Trim()).ToList();
            user.MusicProfile.FavoriteGenres = request.Genres.Split(',').Select(g => g.Trim()).ToList();
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
});
app.MapGet("/users", async (AppDbContext db, [FromQuery] Guid? userId, [FromQuery] int? count) =>
{
    try
    {
        // ✅ Validate required userId
        if (userId == null || userId == Guid.Empty)
        {
            return Results.BadRequest(new TakeExUsersResponse
            {
                Success = false,
                Count = 0,
                Users = new(),
                Message = "Missing or invalid userId query parameter"
            });
        }

        Guid currentUserId = userId.Value;
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
// Add this endpoint to Program.cs for quick database population
// Place this AFTER your other endpoints but BEFORE app.Run()
app.MapPost("/dev/populate-users", async (AppDbContext db, int count = 50) =>
{
    try
    {
        Console.WriteLine($"🚀 Creating {count} temporary users...");

        var random = new Random();
        var names = new[] { "Alex", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Avery", "Quinn", "Sam", "Drew" };
        var surnames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez" };
        var genres = new[] { "Pop", "Rock", "Hip Hop", "Jazz", "Electronic", "Classical", "Metal", "R&B", "Indie", "Country", "Latin", "K-Pop", "Soul", "Punk", "Reggae" };
        var artists = new[] {
            "Taylor Swift", "Drake", "Arctic Monkeys", "Beyoncé", "Eminem",
            "Daft Punk", "Bad Bunny", "The Weeknd", "Billie Eilish", "Post Malone",
            "Ed Sheeran", "Ariana Grande", "Bruno Mars", "Adele", "Coldplay",
            "Imagine Dragons", "Twenty One Pilots", "Kanye West", "Travis Scott", "SZA"
        };
        var songs = new[] {
            "Anti-Hero", "Blinding Lights", "Do I Wanna Know", "One More Time",
            "HUMBLE.", "Enter Sandman", "Creep", "Everlong", "Bohemian Rhapsody",
            "Smells Like Teen Spirit", "Sweet Child O' Mine", "Stairway to Heaven",
            "Hotel California", "Imagine", "Hey Jude", "Purple Rain", "Billie Jean"
        };
        var locations = new[] {
            "New York, NY", "Los Angeles, CA", "Austin, TX", "Seattle, WA",
            "Miami, FL", "Chicago, IL", "London, UK", "Berlin, DE", "Paris, FR",
            "Tel Aviv, IL", "Tokyo, JP", "Sydney, AU", "Toronto, CA", "Barcelona, ES"
        };
        var bios = new[] {
            "Music is my life 🎵",
            "Looking for someone who shares my taste in music",
            "Concert buddy wanted!",
            "Vinyl collector and coffee enthusiast ☕",
            "Let's make a playlist together",
            "Music festival addict 🎪",
            "Always discovering new artists",
            "Live music > recorded music",
            "Spotify wrapped champion 🏆",
            "My headphones are my best friend"
        };

        var users = new List<User>();
        var hasher = new PasswordHasher<User>();

        for (int i = 0; i < count; i++)
        {
            var firstName = names[random.Next(names.Length)];
            var lastName = surnames[random.Next(surnames.Length)];
            var name = $"{firstName} {lastName}";
            var email = $"{firstName.ToLower()}.{lastName.ToLower()}{random.Next(1000, 9999)}@temp.com";

            var favoriteGenres = genres.OrderBy(_ => random.Next()).Take(random.Next(3, 6)).ToList();
            var favoriteArtists = artists.OrderBy(_ => random.Next()).Take(random.Next(5, 10)).ToList();
            var favoriteSongs = songs.OrderBy(_ => random.Next()).Take(random.Next(5, 10)).ToList();

            var user = new User
            {
                Name = name,
                Email = email,
                Age = random.Next(18, 45),
                Gender = random.Next(2) == 0 ? "Male" : "Female",
                Location = locations[random.Next(locations.Length)],
                Bio = bios[random.Next(bios.Length)],
                PasswordHash = hasher.HashPassword(null!, "TempPass123!"),
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow.AddDays(-random.Next(0, 30)),
                MusicProfile = new MusicProfile
                {
                    FavoriteGenres = favoriteGenres,
                    FavoriteArtists = favoriteArtists,
                    FavoriteSongs = favoriteSongs
                }
            };

            users.Add(user);
        }

        await db.Users.AddRangeAsync(users);
        await db.SaveChangesAsync();

        // Add images for each user
        var userImages = new List<UserImage>();
        foreach (var user in users)
        {
            var imageCount = random.Next(1, 5); // 1-4 images per user
            for (int i = 1; i <= imageCount; i++)
            {
                userImages.Add(new UserImage
                {
                    UserId = user.Id,
                    ImageUrl = $"https://i.pravatar.cc/400?img={random.Next(1, 70)}"
                });
            }
        }

        await db.UserImages.AddRangeAsync(userImages);
        await db.SaveChangesAsync();

        var totalUsers = await db.Users.CountAsync();

        return Results.Ok(new
        {
            success = true,
            message = $"Successfully created {count} temporary users",
            createdCount = count,
            totalUsers = totalUsers,
            sampleUsers = users.Take(5).Select(u => new
            {
                u.Id,
                u.Name,
                u.Email,
                u.Age,
                u.Location,
                MusicProfile = new
                {
                    Genres = u.MusicProfile?.FavoriteGenres?.Take(3).ToList() ?? new List<string>(),
                    Artists = u.MusicProfile?.FavoriteArtists?.Take(3).ToList() ?? new List<string>()
                }
            })
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error creating users: {ex.Message}\n{ex.StackTrace}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to create users",
            statusCode: 500
        );
    }
})
.WithName("PopulateTestUsers")
.WithSummary("Create temporary test users (Development only)")
.WithDescription("Creates N test users with random profiles. Default: 50 users. Usage: POST /dev/populate-users?count=100");

// Clear all users (use with caution!)
app.MapDelete("/dev/clear-users", async (AppDbContext db) =>
{
    try
    {
        var userCount = await db.Users.CountAsync();

        // Clear all related data in the correct order (respecting foreign keys)
        db.UserSuggestionQueues.RemoveRange(db.UserSuggestionQueues);
        db.Likes.RemoveRange(db.Likes);
        db.UserImages.RemoveRange(db.UserImages);
        db.MusicProfiles.RemoveRange(db.MusicProfiles);
        db.Users.RemoveRange(db.Users);

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            success = true,
            message = $"Cleared {userCount} users and all related data",
            deletedCount = userCount
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "Failed to clear users");
    }
})
.WithName("ClearAllUsers")
.WithSummary("⚠️ DELETE all users (Development only)")
.WithDescription("Removes ALL users and related data from database. Use with extreme caution!");
// ---- Music Profile ----
app.MapPost("/users/{id:guid}/music-profile", async (AppDbContext db, Guid id, MusicProfileDto dto) =>
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
    Location = user.Location!,
    Bio = user.Bio!,
    SexualOrientation = user.SexualOrientation,
    MusicProfile = user.MusicProfile != null ? new MusicProfileDto
    {
        FavoriteGenres = user.MusicProfile.FavoriteGenres!,
        FavoriteArtists = user!.MusicProfile.FavoriteArtists,
        FavoriteSongs = user.MusicProfile!.FavoriteSongs
    } : null,
    Images = [.. user.Images.Select(i => i.ImageUrl ?? i.Url)]
};
static double CalculateLocalCompatibility(MusicProfile p1, MusicProfile p2, User u1, User u2)
{
    // =======================
    // 1. Music scoring
    // =======================
    double genreScore = JaccardSimilarity(p1.FavoriteGenres, p2.FavoriteGenres);
    double artistScore = JaccardSimilarity(p1.FavoriteArtists, p2.FavoriteArtists);
    double songScore = JaccardSimilarity(p1.FavoriteSongs, p2.FavoriteSongs);

    // Weighted music score (total 100)
    double musicScore = genreScore * 0.3 + artistScore * 0.4 + songScore * 0.3;

    // =======================
    // 2. Preference scoring
    // =======================
    double preferenceScore = CalculatePreferenceCompatibility(u1, u2);

    // =======================
    // 3. Combine music (80%) + preference (20%)
    // =======================
    double totalScore = (musicScore * 0.8) + (preferenceScore * 0.2);

    return Math.Round(totalScore, 2);
}

// =======================
// Attraction check
// =======================
static bool IsAttractedTo(User attractor, string attractedToGender)
{
    // Attractor finds the gender attractive if their orientation matches the gender,
    // or if their orientation is "Both" / "Bisexual" / etc.
    if (string.IsNullOrWhiteSpace(attractor.SexualOrientation)) return false;

    var orientation = attractor.SexualOrientation.ToLowerInvariant();
    var gender = attractedToGender.ToLowerInvariant();

    return orientation == "both" || orientation == gender;
}

// =======================
// Preference compatibility
// =======================
static double CalculatePreferenceCompatibility(User u1, User u2)
{
    bool u1_Attracted_To_u2 = IsAttractedTo(u1, u2.Gender);
    bool u2_Attracted_To_u1 = IsAttractedTo(u2, u1.Gender);

    return (u1_Attracted_To_u2 && u2_Attracted_To_u1) ? 100.0 : 0.0;
}

// =======================
// Jaccard similarity for lists
// =======================
static double JaccardSimilarity(List<string>? list1, List<string>? list2)
{
    if (list1 == null || list2 == null || !list1.Any() || !list2.Any()) return 0;

    var set1 = new HashSet<string>(list1.Select(s => s.Trim().ToLowerInvariant()));
    var set2 = new HashSet<string>(list2.Select(s => s.Trim().ToLowerInvariant()));

    int intersection = set1.Intersect(set2).Count();
    int union = set1.Union(set2).Count();

    return union == 0 ? 0 : (double)intersection / union * 100;
}

static async Task UpdateQueueScoresInBackground(Guid userId, List<Guid> suggestedUserIds)
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
    }.ConnectionString;
}
//==========EndPoints=========
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

        Console.WriteLine($"🔐 Callback received - Code present: {!string.IsNullOrEmpty(code)}, Error: {error}");

        if (!string.IsNullOrEmpty(error))
        {
            Console.WriteLine($"❌ Spotify authorization declined: {error}");
            var errorRedirect = "spotilove://auth/error?message=Authorization declined";
            return Results.Redirect(errorRedirect);
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.WriteLine("❌ Missing authorization code");
            var errorRedirect = "spotilove://auth/error?message=Missing authorization code";
            return Results.Redirect(errorRedirect);
        }

        Console.WriteLine("✅ Valid callback code received");

        // Connect to Spotify
        await spotify.ConnectUserAsync(code);
        Console.WriteLine("✅ Connected to Spotify API");

        // Fetch user profile
        var spotifyProfile = await spotify.GetUserProfileAsync();

        if (spotifyProfile == null || string.IsNullOrEmpty(spotifyProfile.Email))
        {
            Console.WriteLine("❌ Failed to fetch Spotify profile");
            var errorRedirect = "spotilove://auth/error?message=Unable to fetch email from Spotify";
            return Results.Redirect(errorRedirect);
        }

        Console.WriteLine($"📧 Spotify email: {spotifyProfile.Email}");

        // Check for existing user
        var existingUser = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Email == spotifyProfile.Email);

        User user;
        bool isNewUser = false;

        if (existingUser == null)
        {
            // NEW USER - Create account
            isNewUser = true;
            Console.WriteLine($"👤 Creating new user for: {spotifyProfile.Email}");

            var randomPassword = Guid.NewGuid().ToString();
            var hashedPassword = hasher.HashPassword(null!, randomPassword);

            user = new User
            {
                Name = spotifyProfile.DisplayName ?? spotifyProfile.Id,
                Email = spotifyProfile.Email,
                PasswordHash = hashedPassword,
                Age = 0,
                Gender = "",
                SexualOrientation = null,
                CreatedAt = DateTime.UtcNow,
                MusicProfile = new MusicProfile
                {
                    FavoriteGenres = new List<string>(),
                    FavoriteArtists = new List<string>(),
                    FavoriteSongs = new List<string>()
                }
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            Console.WriteLine($"✅ New user created: {user.Email} (ID: {user.Id})");
        }
        else
        {
            // EXISTING USER - Update login time
            user = existingUser;
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            Console.WriteLine($"✅ Existing user logged in: {user.Email} (ID: {user.Id})");
        }

        // Generate auth token
        var token = Guid.NewGuid().ToString();

        // Get connection string for background task
        var connectionString = db.Database.GetConnectionString();
        var userId = user.Id;

        // Build deep link
        var deepLinkUrl = $"spotilove://auth/success?token={Uri.EscapeDataString(token)}&userId={user.Id}&isNewUser={isNewUser}&name={Uri.EscapeDataString(user.Name ?? "User")}";

        Console.WriteLine($"🔗 Redirecting to app: {deepLinkUrl}");

        // Start background music sync (don't await)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // Wait for main request to complete

                Console.WriteLine($"🎵 Starting background music sync for user {userId}");

                var topSongs = await spotify.GetUserTopSongsAsync(10);
                var topArtists = await spotify.GetUserTopArtistsWithImagesAsync(10);
                var topGenres = await spotify.GetUserTopGenresAsync(20);

                Console.WriteLine($"🎵 Fetched: {topSongs.Count} songs, {topArtists.Count} artists, {topGenres.Count} genres");

                // Create new DbContext for background task
                var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

                if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
                {
                    var uri = new Uri(connectionString);
                    var userInfo = uri.UserInfo.Split(':', 2);

                    var connStr = $"Host={uri.Host};Port={uri.Port};Database={uri.LocalPath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";

                    optionsBuilder.UseNpgsql(connStr).UseSnakeCaseNamingConvention();
                }
                else
                {
                    optionsBuilder.UseSqlite(connectionString);
                }

                using var bgDb = new AppDbContext(optionsBuilder.Options);

                var bgUser = await bgDb.Users
                    .Include(u => u.MusicProfile)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (bgUser?.MusicProfile != null)
                {
                    bgUser.MusicProfile.FavoriteGenres = topGenres.Select(g => g.Trim()).ToList();
                    bgUser.MusicProfile.FavoriteArtists = topArtists.Select(a => a.Name.Trim()).ToList();
                    bgUser.MusicProfile.FavoriteSongs = topSongs.Select(s => s.Trim()).ToList();

                    await bgDb.SaveChangesAsync();

                    Console.WriteLine($"✅ Music profile updated successfully");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Background music sync failed: {ex.Message}");
            }
        });

        // Return HTML redirect page
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
            background: rgba(0,0,0,0.6);
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
    </script>
</body>
</html>";

        return Results.Content(html, "text/html");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Callback error: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");

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
.WithSummary("Handles Spotify OAuth callback")
.WithDescription("Processes Spotify authorization code and creates/logs in user");

app.MapGet("/login/test", () =>
{
    return Results.Ok(new
    {
        success = true,
        message = "Login endpoint is working",
        timestamp = DateTime.UtcNow
    });
})
.WithName("TestLoginEndpoint")
.WithSummary("Test endpoint to verify login is accessible");

// Helper function for Npgsql connection string
app.MapPut("/users/{userId:guid}/basic-profile", async (
    AppDbContext db,
    Guid userId,
    DtoMappers.BasicProfileUpdateRequest request) =>
{
    try
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return Results.NotFound(new { success = false, message = "User not found" });
        }

        // Update basic profile fields
        user.Age = request.Age;
        user.Gender = request.Gender;
        user.SexualOrientation = request.SexualOrientation;

        if (!string.IsNullOrWhiteSpace(request.Bio))
        {
            user.Bio = request.Bio;
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            success = true,
            message = "Profile updated successfully",
            user = new
            {
                user.Id,
                user.Name,
                user.Age,
                user.Gender,
                user.SexualOrientation,
                user.Bio
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error updating basic profile: {ex.Message}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to update profile",
            statusCode: 500
        );
    }
})
.WithName("UpdateBasicProfile")
.WithSummary("Update user's basic profile information (age, gender, sexual orientation, bio)");

//Take n Example Users from DB
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

app.MapGet("/login", (SpotifyService spotify) =>
{
    try
    {
        var loginUrl = spotify.GetLoginUrl();
        Console.WriteLine($"🔗 Redirecting to Spotify: {loginUrl}");
        return Results.Redirect(loginUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Login error: {ex.Message}");
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to initiate Spotify login",
            statusCode: 500
        );
    }
})
.WithName("SpotifyLogin")
.WithSummary("Initiates Spotify OAuth login")
.WithDescription("Redirects user to Spotify authorization page");

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
        if (user.Id == Guid.Empty)
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

// ---- User Management Endpoints ----
app.MapPost("/users", Endpoints.CreateUser);
app.MapGet("/users/{id:guid}", Endpoints.GetUser);
app.MapPut("/users/{id:guid}/profile", Endpoints.UpdateProfile);
app.MapGet("/users:search", Endpoints.SearchUsers);

// ---- User Images Endpoints ----
app.MapGet("/users/{id:guid}/images", Endpoints.GetUserImages);
app.MapPost("/users/{id:guid}/images", Endpoints.AddUserImage);

// ---- Swiping Endpoints ----
app.MapGet("/swipe/discover/{userId:guid}", SwipeEndpoints.GetPotentialMatches);
app.MapPost("/swipe/{fromUserId:guid}/like/{toUserId:guid}", SwipeEndpoints.LikeUser);
app.MapPost("/swipe/{fromUserId:guid}/pass/{toUserId:guid}", SwipeEndpoints.PassUser);
app.MapGet("/matches/{userId:guid}", SwipeEndpoints.GetUserMatches);
app.MapGet("/swipe/stats/{userId:guid}", SwipeEndpoints.GetSwipeStats);
Console.WriteLine($"📖 View API documentation at: http://localhost:{port}/swagger");

app.Run();
