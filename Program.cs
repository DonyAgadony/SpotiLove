using Microsoft.EntityFrameworkCore;
using Spotilove;
using DotNetEnv;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;
using static Spotilove.AppDbContext;

DotNetEnv.Env.Load(); // load .env file

var builder = WebApplication.CreateBuilder(args);

// ---- Database Config ----
var dbPath = builder.Configuration.GetValue<string>("ConnectionStrings:Sqlite") ?? "Data Source=spotilove.db";
builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseSqlite(dbPath);
});

// ---- API Documentation ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ---- Services ----
builder.Services.AddScoped<SwipeService>(); // Add SwipeService
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();


// SpotifyService setup (Singleton since it holds state)
builder.Services.AddSingleton(provider => new SpotifyService(
    Environment.GetEnvironmentVariable("ClientKey") ?? throw new Exception("ClientKey missing"),
    Environment.GetEnvironmentVariable("ClientSecret") ?? throw new Exception("ClientSecret missing"),
    Environment.GetEnvironmentVariable("RedirectURI") ?? throw new Exception("RedirectURI missing")
));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors("AllowAll");

// ---- Database Migration ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Auto-seed database if empty on startup
    if (!await db.Users.AnyAsync())
    {
        Console.WriteLine("🌱 Database is empty. Auto-seeding with example users...");
        await SeedDatabaseAsync(db);
    }
}

// ---- Development Tools ----
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ---- Server Configuration ----
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Urls.Add($"http://0.0.0.0:{port}");

// ================== API Endpoints ====================
// ---- Health Check ----
app.MapGet("/", () => Results.Ok(new
{
    message = "Spotilove API is running!",
    timestamp = DateTime.UtcNow,
    endpoints = new
    {
        health = "/health",
        test_swipe = "/test/swipe",
        add_example_users = "/test/add-users",
        test_from_database = "/test/database",
        spotify_login = "/login",
        spotify_callback = "/callback",
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
// Add this to your Program.cs

// ---- OPTIMIZED: Batch Compatibility Calculation ----
app.MapPost("/swipe/batch-calculate", async (AppDbContext db, BatchCalculateRequest request) =>
{
    try
    {
        Console.WriteLine($"🎯 Batch calculating compatibility for user {request.CurrentUserId} with {request.UserIds.Count} users...");

        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.CurrentUserId);

        if (currentUser?.MusicProfile == null)
        {
            return Results.BadRequest(new { success = false, message = "Current user not found or has no music profile" });
        }

        // Fetch all target users in ONE query
        var targetUsers = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .Where(u => request.UserIds.Contains(u.Id) && u.MusicProfile != null)
            .AsNoTracking()
            .ToListAsync();

        if (!targetUsers.Any())
        {
            return Results.Ok(new { success = true, results = new List<CompatibilityResult>() });
        }

        // OPTION 1: Use local Jaccard calculation (INSTANT - no API calls)
        var localResults = targetUsers.Select(user => new CompatibilityResult
        {
            UserId = user.Id,
            Name = user.Name,
            Age = user.Age,
            Location = user.Location,
            Bio = user.Bio,
            CompatibilityScore = CalculateLocalCompatibility(currentUser.MusicProfile, user.MusicProfile!),
            MusicProfile = new MusicProfileDto
            {
                FavoriteGenres = user.MusicProfile!.FavoriteGenres,
                FavoriteArtists = user.MusicProfile.FavoriteArtists,
                FavoriteSongs = user.MusicProfile.FavoriteSongs
            },
            Images = user.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
        }).OrderByDescending(r => r.CompatibilityScore).ToList();

        // OPTION 2: Batch Gemini calls in parallel (if you want AI scores)
        // Uncomment this if you want to use Gemini instead:
        /*
        var geminiTasks = targetUsers.Select(async user =>
        {
            var score = await GeminiService.CalculatePercentage(currentUser.MusicProfile, user.MusicProfile!);
            return new CompatibilityResult
            {
                UserId = user.Id,
                Name = user.Name,
                Age = user.Age,
                Location = user.Location,
                Bio = user.Bio,
                CompatibilityScore = score ?? CalculateLocalCompatibility(currentUser.MusicProfile, user.MusicProfile!),
                MusicProfile = new MusicProfileDto
                {
                    FavoriteGenres = user.MusicProfile!.FavoriteGenres,
                    FavoriteArtists = user.MusicProfile.FavoriteArtists,
                    FavoriteSongs = user.MusicProfile.FavoriteSongs
                },
                Images = user.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
            };
        }).ToList();

        var geminiResults = await Task.WhenAll(geminiTasks);
        var results = geminiResults.OrderByDescending(r => r.CompatibilityScore).ToList();
        */

        // Batch insert to queue (ONE database operation)
        var queueItems = localResults.Select((result, index) => new UserSuggestionQueue
        {
            UserId = request.CurrentUserId,
            SuggestedUserId = result.UserId,
            QueuePosition = index,
            CompatibilityScore = result.CompatibilityScore,
            CreatedAt = DateTime.UtcNow
        }).ToList();

        try
        {
            db.UserSuggestionQueues.AddRange(queueItems);
            await db.SaveChangesAsync();
            Console.WriteLine($"✅ Batch inserted {queueItems.Count} compatibility scores");
        }
        catch (DbUpdateException ex)
        {
            Console.WriteLine($"⚠️ Some users already in queue: {ex.InnerException?.Message}");
        }

        return Results.Ok(new
        {
            success = true,
            processedCount = localResults.Count,
            results = localResults
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Batch calculation error: {ex.Message}");
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("BatchCalculateCompatibility")
.WithSummary("Calculate compatibility scores for multiple users at once");
app.MapGet("/users", async (AppDbContext db, HttpRequest request) =>
{
    try
    {
        // Parse query parameters
        if (!request.Query.ContainsKey("userId") ||
            !int.TryParse(request.Query["userId"], out int currentUserId))
        {
            return Results.BadRequest(new TakeExUsersResponse
            {
                Success = false,
                Count = 0,
                Users = new()
            });
        }

        // Optional: Allow requesting more users (default 10, max 50)
        int requestedCount = 10;
        if (request.Query.ContainsKey("count") &&
            int.TryParse(request.Query["count"], out int count))
        {
            requestedCount = Math.Clamp(count, 1, 50);
        }

        // OPTIMIZATION 1: Single query for current user
        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        if (currentUser?.MusicProfile == null)
        {
            return Results.NotFound(new TakeExUsersResponse
            {
                Success = false,
                Count = 0,
                Users = new()
            });
        }

        // OPTIMIZATION 2: Parallel data fetching (3 queries at once)
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

        // OPTIMIZATION 3: Check if we need to populate queue
        var queuedUserIds = queueItems.Select(q => q.SuggestedUserId).ToHashSet();
        bool needsQueueRefill = queueItems.Count < requestedCount * 2;

        if (needsQueueRefill && totalAvailable > swipedUserIds.Count + queuedUserIds.Count)
        {
            // BATCH PROCESS: Calculate scores for multiple users at once
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

                // Fetch all candidates in ONE query
                var candidates = await db.Users
                    .Include(u => u.MusicProfile)
                    .Where(u => candidateIds.Contains(u.Id))
                    .AsNoTracking()
                    .ToListAsync();

                // PARALLEL LOCAL CALCULATION (instant, no API calls)
                var scoredCandidates = candidates
                    .AsParallel() // Use parallel processing for speed
                    .Select(user => new
                    {
                        UserId = user.Id,
                        Score = CalculateLocalCompatibility(
                            currentUser.MusicProfile,
                            user.MusicProfile!)
                    })
                    .OrderByDescending(x => x.Score)
                    .ToList();

                // BATCH INSERT to queue (single DB operation)
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
                    db.UserSuggestionQueues.AddRange(batchInserts);
                    await db.SaveChangesAsync();
                    Console.WriteLine($"✅ Batch inserted {batchInserts.Count} queue items");

                    // Merge new items into queue for immediate use
                    queueItems.AddRange(batchInserts);
                    queueItems = queueItems
                        .OrderByDescending(q => q.CompatibilityScore)
                        .Take(requestedCount * 3)
                        .ToList();

                    // Fire-and-forget: Update with Gemini scores in background
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
                catch (DbUpdateException ex)
                {
                    Console.WriteLine($"⚠️ Batch insert conflict: {ex.InnerException?.Message}");
                }
            }
        }

        // OPTIMIZATION 4: Get top suggestions from queue
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

        // OPTIMIZATION 5: Fetch user details in ONE query
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .Where(u => topSuggestionIds.Contains(u.Id))
            .AsNoTracking()
            .ToListAsync();

        // Preserve queue ordering
        var userDict = users.ToDictionary(u => u.Id);
        var orderedUsers = topSuggestionIds
            .Where(id => userDict.ContainsKey(id))
            .Select(id => userDict[id])
            .ToList();

        // Convert to DTOs
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
.WithDescription("Returns users sorted by compatibility score. Supports ?userId={id}&count={1-50}");

// ===== HELPER FUNCTIONS =====

static UserDto ToUserDto(User user) => new()
{
    Id = user.Id,
    Name = user.Name,
    Email = user.Email,
    Age = user.Age,
    Location = user.Location,
    Bio = user.Bio,
    MusicProfile = new MusicProfileDto
    {
        FavoriteGenres = user.MusicProfile!.FavoriteGenres,
        FavoriteArtists = user.MusicProfile.FavoriteArtists,
        FavoriteSongs = user.MusicProfile.FavoriteSongs
    },
    Images = user.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
};

static double CalculateLocalCompatibility(MusicProfile p1, MusicProfile p2)
{
    double genreScore = JaccardSimilarity(p1.FavoriteGenres, p2.FavoriteGenres);
    double artistScore = JaccardSimilarity(p1.FavoriteArtists, p2.FavoriteArtists);
    double songScore = JaccardSimilarity(p1.FavoriteSongs, p2.FavoriteSongs);

    return Math.Round((genreScore * 30 + artistScore * 40 + songScore * 30));
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
        Console.WriteLine($"🤖 Starting background Gemini updates for {suggestedUserIds.Count} users...");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=spotilove.db");

        using var db = new AppDbContext(optionsBuilder.Options);

        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (currentUser?.MusicProfile == null)
        {
            Console.WriteLine("⚠️ Current user not found in background task");
            return;
        }

        int successCount = 0;
        int failCount = 0;

        // Process users with rate limiting
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
                    Console.WriteLine($"⚠️ Suggested user {suggestedId} not found");
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
                        Console.WriteLine($"✅ Gemini score for user {suggestedId}: {geminiScore}% (was {queueItem.CompatibilityScore}%)");
                    }
                }
                else
                {
                    failCount++;
                    Console.WriteLine($"⚠️ Gemini returned null for user {suggestedId}");
                }

                // Rate limit: 500ms between calls
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                failCount++;
                Console.WriteLine($"❌ Gemini failed for user {suggestedId}: {ex.Message}");
            }
        }

        Console.WriteLine($"🎯 Background update complete: {successCount} success, {failCount} failed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Background update error: {ex.Message}\n{ex.StackTrace}");
    }
}
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
    // Check if email already exists
    if (await db.Users.AnyAsync(u => u.Email == request.Email))
    {
        return Results.BadRequest(new { success = false, message = "Email already exists" });
    }

    // Hash password
    var hashedPassword = hasher.HashPassword(null!, request.Password);

    // Create user
    var user = new User
    {
        Name = request.Name,
        Email = request.Email,
        PasswordHash = hashedPassword,
        Age = request.Age,
        Gender = request.Gender
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    // You could generate a JWT token here if you want
    var token = Guid.NewGuid().ToString(); // Placeholder

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
            user.Gender
        }
    });
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

// Keep all other endpoints as they were...
// [Rest of your endpoints remain the same]

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

// ---- Spotify Integration Endpoints ----
app.MapGet("/login", (SpotifyService spotify) =>
{
    var url = spotify.GetLoginUrl();
    return Results.Redirect(url);
});

app.MapGet("/callback", async (HttpRequest req, SpotifyService spotify, AppDbContext db) =>
{
    var code = req.Query["code"].ToString();
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest("Missing code");

    await spotify.ConnectUserAsync(code);
    var updatedUser = await spotify.UpdateUserProfileInDb(db, 1);

    return Results.Ok(new { Message = "Spotify connected!", User = updatedUser });
});

Console.WriteLine("🎯 Spotilove API is starting...");
Console.WriteLine($"🌐 Running on port: {port}");
Console.WriteLine("🤖 Gemini AI compatibility calculation enabled");
Console.WriteLine("⚙️  Make sure to set GeminiAPIKey environment variable for AI features");
Console.WriteLine("🧪 Test endpoints available:");
Console.WriteLine("   - POST /test/add-users - Add example users to database");
Console.WriteLine("   - POST /seed-database - Seed database with users");
Console.WriteLine($"📖 View API documentation at: http://0.0.0.0:{port}/swagger");

app.Run();

// Helper method to seed database
static async Task SeedDatabaseAsync(AppDbContext db)
{
    // Check if users already exist
    var existingCount = await db.Users.CountAsync();
    if (existingCount >= 100)
    {
        Console.WriteLine($"⚠️ Database already has {existingCount} users. Skipping seed.");
        return;
    }

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

        var user = new User
        {
            Name = name,
            Email = email,
            Age = age,
            Location = location,
            Bio = bio,
            MusicProfile = new MusicProfile
            {
                FavoriteSongs = string.Join(", ", sampleSongs.OrderBy(_ => random.Next()).Take(3)),
                FavoriteArtists = string.Join(", ", sampleArtists.OrderBy(_ => random.Next()).Take(3)),
                FavoriteGenres = string.Join(", ", sampleGenres.OrderBy(_ => random.Next()).Take(3))
            }
        };

        users.Add(user);
    }

    await db.Users.AddRangeAsync(users);
    await db.SaveChangesAsync();

    // Add images
    var userImages = new List<UserImage>();
    foreach (var user in users)
    {
        userImages.Add(new UserImage
        {
            UserId = user.Id,
            ImageUrl = $"https://picsum.photos/400/600?random={user.Id}1"
        });
        userImages.Add(new UserImage
        {
            UserId = user.Id,
            ImageUrl = $"https://picsum.photos/400/600?random={user.Id}2"
        });
    }

    await db.UserImages.AddRangeAsync(userImages);
    await db.SaveChangesAsync();

    Console.WriteLine($"✅ Seeded database with {users.Count} users");
}