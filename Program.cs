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
app.Urls.Add("http:// 192.168.25.86:5106");

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
        users = "/users",
        user_images = "/users/{id}/images",
        swipe_discover = "/swipe/discover/{userId}",
        swipe_action = "/swipe",
        swipe_like = "/swipe/{fromUserId}/like/{toUserId}",
        swipe_pass = "/swipe/{fromUserId}/pass/{toUserId}",
        matches = "/matches/{userId}",
        swipe_stats = "/swipe/stats/{userId}",
        likes = "/likes",
        swagger = "/swagger"
    }
}));
app.MapPost("/swipe", async (AppDbContext db, LikeDto dto) =>
{
    var service = new SwipeService(db);
    var result = await service.SwipeAsync(dto.FromUserId, dto.ToUserId, dto.IsLike);
    return Results.Ok(result);
});
app.MapGet("/users", async (AppDbContext db, HttpRequest request) =>
{
    if (!request.Query.ContainsKey("userId"))
        return Results.BadRequest("Missing userId");

    if (!int.TryParse(request.Query["userId"], out int currentUserId))
        return Results.BadRequest("Invalid userId");

    var currentUser = await db.Users
        .Include(u => u.MusicProfile)
        .Include(u => u.Images)
        .FirstOrDefaultAsync(u => u.Id == currentUserId);

    if (currentUser == null || currentUser.MusicProfile == null)
        return Results.NotFound("User or music profile not found");

    // Get all users the current user already swiped on
    var swipedUserIds = await db.Likes
        .Where(l => l.FromUserId == currentUserId)
        .Select(l => l.ToUserId)
        .ToListAsync();

    // Get users already in the suggestion queue
    var queueItems = await db.UserSuggestionQueues
        .Where(q => q.UserId == currentUserId)
        .OrderBy(q => q.QueuePosition)
        .ToListAsync();

    var suggestions = new List<UserDto>();
    int positionCounter = queueItems.Any() ? queueItems.Max(q => q.QueuePosition) + 1 : 0;

    var otherUsers = await db.Users
        .Include(u => u.MusicProfile)
        .Include(u => u.Images)
        .Where(u => u.Id != currentUserId && u.MusicProfile != null && !swipedUserIds.Contains(u.Id))
        .ToListAsync();

    foreach (var user in otherUsers)
    {
        // Skip if already in queue
        var existingQueueItem = queueItems.FirstOrDefault(q => q.SuggestedUserId == user.Id);
        double score;

        if (existingQueueItem != null)
        {
            score = existingQueueItem.CompatibilityScore;
        }
        else
        {
            int? percentage = await GeminiService.CalculatePercentage(currentUser.MusicProfile, user.MusicProfile!);
            score = percentage ?? 0;

            // Add to queue with position
            var queueItem = new UserSuggestionQueue
            {
                UserId = currentUserId,
                SuggestedUserId = user.Id,
                QueuePosition = positionCounter++,
                CompatibilityScore = score,
                CreatedAt = DateTime.UtcNow
            };

            db.UserSuggestionQueues.Add(queueItem);
            await db.SaveChangesAsync();
        }

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

    // Ensure at least 10 suggestions
    if (suggestions.Count < 10)
    {
        var remaining = otherUsers
            .Where(u => !suggestions.Any(s => s.Id == u.Id))
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
            });

        suggestions.AddRange(remaining);
    }

    return Results.Ok(new TakeExUsersResponse
    {
        Success = true,
        Count = suggestions.Count,
        Users = suggestions
    });
});

app.MapGet("/health", () => Results.Ok(new { ok = true, now = DateTime.UtcNow }));
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

        // Clear existing data first

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
.WithDescription("Clears existing data and adds 100 example users with random profiles for testing");
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
    var result = hasher.VerifyHashedPassword(user, user.PasswordHash!, request.Password);
    if (result == PasswordVerificationResult.Failed)
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
    // (right now, it’s just informational)

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
app.MapGet("/users/{id:int}", Endpoints.GetUser);
app.MapPut("/users/{id:int}/profile", Endpoints.UpdateProfile);
app.MapGet("/users:search", Endpoints.SearchUsers);

// ---- User Images Endpoints ----
app.MapPost("/users/{id:int}/images", Endpoints.AddUserImage);
app.MapGet("/users/{id:int}/images", Endpoints.GetUserImages);

// ---- Spotify Integration Endpoints ----
// Step 1: Redirect user to Spotify login
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

// Optional: Get current user's Spotify data (if already connected)
app.MapGet("/spotify/profile", async (SpotifyService spotifyService) =>
{
    try
    {
        var topArtists = await spotifyService.GetUserTopArtistsAsync();
        var topSongs = await spotifyService.GetUserTopSongsAsync();

        return Results.Ok(new
        {
            TopArtists = topArtists,
            FavoriteGenres = topSongs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "User not connected to Spotify");
    }
});

Console.WriteLine("🎯 Spotilove API is starting...");
Console.WriteLine("🤖 Gemini AI compatibility calculation enabled");
Console.WriteLine("⚙️  Make sure to set GeminiAPIKey environment variable for AI features");
Console.WriteLine("🧪 Test endpoints available:");
Console.WriteLine("   - POST /test/add-users - Add example users to database");
Console.WriteLine("   - GET /test/database - Test with existing database users");
Console.WriteLine("   - GET /test/swipe - Complete test with Gemini AI compatibility");
Console.WriteLine("📖 View API documentation at: http://localhost:5106/swagger");

app.Run();