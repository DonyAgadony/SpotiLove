using Microsoft.EntityFrameworkCore;
using Spotilove;
using DotNetEnv;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;

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

// OPTIMIZED /users endpoint - Replace your existing one
app.MapGet("/users", async (AppDbContext db, HttpRequest request) =>
{
    try
    {
        if (!request.Query.ContainsKey("userId") || !int.TryParse(request.Query["userId"], out int currentUserId))
        {
            return Results.BadRequest(new TakeExUsersResponse { Success = false, Count = 0, Users = new() });
        }

        // OPTIMIZATION 1: Single query to get current user with profile
        var currentUser = await db.Users
            .Include(u => u.MusicProfile)
            .AsNoTracking()  // No tracking for read-only data
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        if (currentUser?.MusicProfile == null)
        {
            return Results.NotFound(new TakeExUsersResponse { Success = false, Count = 0, Users = new() });
        }

        // OPTIMIZATION 2: Parallel queries for swiped and queued users
        var swipedTask = db.Likes
     .AsNoTracking() // ✅ Apply here while still entity query
     .Where(l => l.FromUserId == currentUserId)
     .Select(l => l.ToUserId)
     .ToListAsync();

        var queueTask = db.UserSuggestionQueues
            .Where(q => q.UserId == currentUserId)
            .OrderByDescending(q => q.CompatibilityScore)
            .AsNoTracking()
            .ToListAsync();

        await Task.WhenAll(swipedTask, queueTask);

        var swipedUserIds = swipedTask.Result.ToHashSet();
        var queueItems = queueTask.Result;
        var queuedUserIds = queueItems.Select(q => q.SuggestedUserId).ToHashSet();

        var suggestions = new List<UserDto>();

        // OPTIMIZATION 3: Return users from existing queue first (no Gemini calls needed)
        var queueUserIds = queueItems.Where(q => q.CompatibilityScore >= 60).Take(10).Select(q => q.SuggestedUserId).ToList();

        if (queueUserIds.Any())
        {
            var queuedUsers = await db.Users
                .Include(u => u.MusicProfile)
                .Include(u => u.Images)
                .Where(u => queueUserIds.Contains(u.Id))
                .AsNoTracking()
                .ToListAsync();

            suggestions.AddRange(queuedUsers.Select(u => new UserDto
            {
                Id = u.Id,
                Name = u.Name,
                Email = u.Email,
                Age = u.Age,
                Location = u.Location,
                Bio = u.Bio,
                MusicProfile = new MusicProfileDto
                {
                    FavoriteGenres = u.MusicProfile!.FavoriteGenres,
                    FavoriteArtists = u.MusicProfile.FavoriteArtists,
                    FavoriteSongs = u.MusicProfile.FavoriteSongs
                },
                Images = u.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
            }));
        }

        // OPTIMIZATION 4: Only fetch new users if we need more suggestions
        if (suggestions.Count < 10)
        {
            var needed = 10 - suggestions.Count;

            // Get new users batch (limit to 5 to avoid too many Gemini calls)
            var newUsers = await db.Users
                .Include(u => u.MusicProfile)
                .Include(u => u.Images)
                .Where(u => u.Id != currentUserId &&
                           u.MusicProfile != null &&
                           !swipedUserIds.Contains(u.Id) &&
                           !queuedUserIds.Contains(u.Id))
                .AsNoTracking()
                .Take(Math.Min(needed, 5))  // Limit Gemini calls
                .ToListAsync();

            // OPTIMIZATION 5: Batch process with background queue updates
            var batchInserts = new List<UserSuggestionQueue>();
            int positionCounter = queueItems.Any() ? queueItems.Max(q => q.QueuePosition) + 1 : 0;

            foreach (var user in newUsers)
            {
                // Use local Jaccard calculation as fallback (instant, no API call)
                double score = CalculateLocalCompatibility(currentUser.MusicProfile, user.MusicProfile!);

                // Add to batch insert list
                batchInserts.Add(new UserSuggestionQueue
                {
                    UserId = currentUserId,
                    SuggestedUserId = user.Id,
                    QueuePosition = positionCounter++,
                    CompatibilityScore = score,
                    CreatedAt = DateTime.UtcNow
                });

                if (score >= 60)
                {
                    suggestions.Add(new UserDto
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
                    });
                }
            }

            // OPTIMIZATION 6: Batch insert to queue (single DB call)
            if (batchInserts.Any())
            {
                try
                {
                    db.UserSuggestionQueues.AddRange(batchInserts);
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    Console.WriteLine($"⚠️ Batch insert warning: {ex.InnerException?.Message}");
                    // Continue anyway - we have suggestions to return
                }
            }

            // OPTIMIZATION 7: Fire-and-forget Gemini updates in background
            _ = Task.Run(async () => await UpdateQueueScoresInBackground(currentUserId, batchInserts.Select(b => b.SuggestedUserId).ToList()));
        }

        // Fill with random users if still under 10
        if (suggestions.Count < 10)
        {
            var remaining = await db.Users
                .Include(u => u.MusicProfile)
                .Include(u => u.Images)
                .Where(u => u.Id != currentUserId &&
                           u.MusicProfile != null &&
                           !swipedUserIds.Contains(u.Id) &&
                           !suggestions.Select(s => s.Id).Contains(u.Id))
                .AsNoTracking()
                .OrderBy(_ => Guid.NewGuid())
                .Take(10 - suggestions.Count)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    Name = u.Name,
                    Email = u.Email,
                    Age = u.Age,
                    Location = u.Location,
                    Bio = u.Bio,
                    MusicProfile = new MusicProfileDto
                    {
                        FavoriteGenres = u.MusicProfile!.FavoriteGenres,
                        FavoriteArtists = u.MusicProfile.FavoriteArtists,
                        FavoriteSongs = u.MusicProfile.FavoriteSongs
                    },
                    Images = u.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
                })
                .ToListAsync();

            suggestions.AddRange(remaining);
        }

        return Results.Ok(new TakeExUsersResponse
        {
            Success = true,
            Count = suggestions.Count,
            Users = suggestions
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error: {ex.Message}");
        return Results.Ok(new TakeExUsersResponse { Success = false, Count = 0, Users = new() });
    }
})
.WithName("GetUsersForSwipe");

// OPTIMIZATION 8: Local Jaccard calculation (instant fallback)
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

// OPTIMIZATION 9: Background Gemini updates (non-blocking)
static async Task UpdateQueueScoresInBackground(int userId, List<int> suggestedUserIds)
{
    try
    {
        // Create a new scope for background work
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=spotilove.db");

        using var db = new AppDbContext(optionsBuilder.Options);

        var currentUser = await db.Users.Include(u => u.MusicProfile).FirstOrDefaultAsync(u => u.Id == userId);
        if (currentUser?.MusicProfile == null) return;

        foreach (var suggestedId in suggestedUserIds)
        {
            var suggestedUser = await db.Users.Include(u => u.MusicProfile).FirstOrDefaultAsync(u => u.Id == suggestedId);
            if (suggestedUser?.MusicProfile == null) continue;

            try
            {
                var geminiScore = await GeminiService.CalculatePercentage(currentUser.MusicProfile, suggestedUser.MusicProfile);

                if (geminiScore.HasValue)
                {
                    var queueItem = await db.UserSuggestionQueues
                        .FirstOrDefaultAsync(q => q.UserId == userId && q.SuggestedUserId == suggestedId);

                    if (queueItem != null)
                    {
                        queueItem.CompatibilityScore = geminiScore.Value;
                        await db.SaveChangesAsync();
                        Console.WriteLine($"✅ Updated score for user {suggestedId}: {geminiScore}%");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Background Gemini update failed for user {suggestedId}: {ex.Message}");
            }

            // Rate limit: wait between calls
            await Task.Delay(500);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Background update error: {ex.Message}");
    }
}
app.MapGet("/health", () => Results.Ok(new { ok = true, now = DateTime.UtcNow }));


// 🧩 Helper
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