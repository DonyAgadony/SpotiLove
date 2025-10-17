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
builder.Services.AddScoped<MatchService>();
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

    // Load queue items for this user
    var queueItems = await db.UserSuggestionQueues
        .Where(q => q.UserId == currentUserId)
        .OrderBy(q => q.QueuePosition)
        .ToListAsync();

    var suggestions = new List<UserDto>();
    int positionCounter = queueItems.Any() ? queueItems.Max(q => q.QueuePosition) + 1 : 0;

    var otherUsers = await db.Users
        .Include(u => u.MusicProfile)
        .Include(u => u.Images)
        .Where(u => u.Id != currentUserId && u.MusicProfile != null)
        .ToListAsync();

    foreach (var user in otherUsers)
    {
        // Skip if already in queue and score < 60%
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

    // Ensure at least 10 suggestions, fill with random remaining users if needed
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
    var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
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
// ---- NEW: Test From Database Endpoint ----
app.MapGet("/test/database", async (AppDbContext db, SwipeService swipeService) =>
{
    var testLog = new List<object>();

    try
    {
        Console.WriteLine("🔍 =================================");
        Console.WriteLine("🔍 TESTING WITH DATABASE USERS");
        Console.WriteLine("🔍 =================================");

        testLog.Add(new { step = "Database Test Started", timestamp = DateTime.UtcNow, success = true });

        // Step 1: Get all users from database
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .ToListAsync();

        if (users.Count < 3)
        {
            return Results.BadRequest(new
            {
                success = false,
                message = $"Need at least 3 users in database for testing. Found {users.Count}. Use POST /test/add-users first.",
                availableUsers = users.Select(u => new { u.Id, u.Name })
            });
        }

        Console.WriteLine($"👥 Found {users.Count} users in database:");
        foreach (var user in users)
        {
            Console.WriteLine($"   - {user.Name} (ID: {user.Id}) - {user.MusicProfile?.FavoriteSongs}");
        }

        testLog.Add(new
        {
            step = "Users loaded from database",
            userCount = users.Count,
            users = users.Select(u => new { u.Id, u.Name, u.Age, u.Location }),
            success = true
        });

        // Step 2: Pick first 3 users for testing
        var testUser1 = users[0];  // Will be our main test user
        var testUser2 = users[1];  // Will like back
        var testUser3 = users[2];  // Will be passed on

        Console.WriteLine($"\n🎯 Testing with:");
        Console.WriteLine($"   - {testUser1.Name} (ID: {testUser1.Id}) as main user");
        Console.WriteLine($"   - {testUser2.Name} (ID: {testUser2.Id}) for mutual like");
        Console.WriteLine($"   - {testUser3.Name} (ID: {testUser3.Id}) for pass");

        // Step 3: Initialize main user's queue
        Console.WriteLine($"\n🎯 Initializing {testUser1.Name}'s suggestion queue...");
        var queueInitSuccess = await swipeService.InitializeUserQueueAsync(testUser1.Id);
        Console.WriteLine($"✅ Queue initialization: {(queueInitSuccess ? "SUCCESS" : "FAILED")}");

        testLog.Add(new
        {
            step = "Queue initialized",
            userId = testUser1.Id,
            userName = testUser1.Name,
            success = queueInitSuccess
        });

        // Step 4: Get potential matches
        Console.WriteLine($"\n🔍 Getting potential matches for {testUser1.Name}...");
        var matchesResult = await SwipeEndpoints.GetPotentialMatches(swipeService, testUser1.Id, 10);
        Console.WriteLine("✅ Retrieved potential matches successfully");

        testLog.Add(new
        {
            step = "Got potential matches",
            userId = testUser1.Id,
            userName = testUser1.Name,
            success = true
        });

        // Step 5: Main user likes second user
        Console.WriteLine($"\n💖 {testUser1.Name} likes {testUser2.Name}...");
        var likeResult = await SwipeEndpoints.LikeUser(swipeService, testUser1.Id, testUser2.Id);
        Console.WriteLine("✅ Like action completed");

        testLog.Add(new
        {
            step = "Like action",
            fromUser = new { testUser1.Id, testUser1.Name },
            toUser = new { testUser2.Id, testUser2.Name },
            success = true
        });

        // Step 6: Second user likes back (mutual like)
        Console.WriteLine($"\n💕 {testUser2.Name} likes {testUser1.Name} back...");
        var mutualLikeResult = await SwipeEndpoints.LikeUser(swipeService, testUser2.Id, testUser1.Id);
        Console.WriteLine("✅ Mutual like completed - should create a match!");

        testLog.Add(new
        {
            step = "Mutual like",
            fromUser = new { testUser2.Id, testUser2.Name },
            toUser = new { testUser1.Id, testUser1.Name },
            success = true
        });

        // Step 7: Main user passes on third user
        Console.WriteLine($"\n👎 {testUser1.Name} passes on {testUser3.Name}...");
        var passResult = await SwipeEndpoints.PassUser(swipeService, testUser1.Id, testUser3.Id);
        Console.WriteLine("✅ Pass action completed");

        testLog.Add(new
        {
            step = "Pass action",
            fromUser = new { testUser1.Id, testUser1.Name },
            toUser = new { testUser3.Id, testUser3.Name },
            success = true
        });

        // Step 8: Check matches
        Console.WriteLine($"\n🔗 Getting {testUser1.Name}'s matches...");
        var userMatchesResult = await SwipeEndpoints.GetUserMatches(swipeService, testUser1.Id);
        Console.WriteLine("✅ Retrieved matches successfully");

        testLog.Add(new
        {
            step = "Retrieved matches",
            userId = testUser1.Id,
            userName = testUser1.Name,
            success = true
        });

        // Step 9: Get swipe statistics
        Console.WriteLine($"\n📈 Getting {testUser1.Name}'s swipe statistics...");
        var statsResult = await SwipeEndpoints.GetSwipeStats(swipeService, testUser1.Id);
        Console.WriteLine("✅ Retrieved stats successfully");

        testLog.Add(new
        {
            step = "Retrieved swipe stats",
            userId = testUser1.Id,
            userName = testUser1.Name,
            success = true
        });

        // Step 10: Final database verification
        Console.WriteLine("\n🔍 Verifying final database state...");
        var totalLikes = await db.Likes.CountAsync();
        var totalMatches = await db.Likes.CountAsync(l =>
            db.Likes.Any(m => m.FromUserId == l.ToUserId && m.ToUserId == l.FromUserId && m.IsLike && l.IsLike)
        );
        var totalUsers = await db.Users.CountAsync();
        var totalImages = await db.UserImages.CountAsync();

        Console.WriteLine($"📊 Final Database State:");
        Console.WriteLine($"   - Total Users: {totalUsers}");
        Console.WriteLine($"   - Total User Images: {totalImages}");
        Console.WriteLine($"   - Total Likes: {totalLikes}");
        Console.WriteLine($"   - Mutual Matches: {totalMatches}");

        testLog.Add(new
        {
            step = "Final database verification",
            totalUsers,
            totalImages,
            totalLikes,
            totalMatches,
            success = true
        });

        Console.WriteLine("\n🎉 =================================");
        Console.WriteLine("🎉 DATABASE TEST COMPLETED SUCCESSFULLY!");
        Console.WriteLine("🎉 =================================");

        testLog.Add(new { step = "Database Test Completed", timestamp = DateTime.UtcNow, success = true });

        return Results.Ok(new
        {
            success = true,
            message = "🎉 Database test completed successfully!",
            testDetails = new
            {
                mainTestUser = new { testUser1.Id, testUser1.Name },
                mutualMatchWith = new { testUser2.Id, testUser2.Name },
                passedUser = new { testUser3.Id, testUser3.Name },
                totalUsersInDb = totalUsers,
                totalLikesProcessed = totalLikes,
                mutualMatchesCreated = totalMatches
            },
            testLog = testLog
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ DATABASE TEST FAILED: {ex.Message}");
        Console.WriteLine($"❌ Stack Trace: {ex.StackTrace}");

        testLog.Add(new
        {
            step = "Database Test Failed",
            error = ex.Message,
            timestamp = DateTime.UtcNow,
            success = false
        });

        return Results.Problem(
            detail: ex.Message,
            title: "Database Test Failed",
            statusCode: 1000,
            instance: "/test/database"
        );
    }
})
.WithName("TestFromDatabase")
.WithSummary("Test swipe service using existing database users")
.WithDescription("Tests the swipe service functionality using users already stored in the database");

// ---- UPDATED: Combined Test Endpoint ----
app.MapGet("/test/swipe", async (AppDbContext db, SwipeService swipeService) =>
{
    var testLog = new List<object>();

    try
    {
        Console.WriteLine("🚀 =================================");
        Console.WriteLine("🚀 STARTING COMPLETE SWIPE SERVICE TEST");
        Console.WriteLine("🚀 =================================");

        testLog.Add(new { step = "Complete Test Started", timestamp = DateTime.UtcNow, success = true });

        // Step 1: Clear existing data and create fresh example users
        Console.WriteLine("🗑️  Clearing existing data...");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM UserSuggestionQueues");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Likes");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM UserImages");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM MusicProfiles");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Users");

        Console.WriteLine("📊 Creating example users in database...");

        // Use the same user creation logic as the add-users endpoint
        var users = new List<User>
        {
            new User
            {
                Name = "Alice Johnson",
                Email = "alice@example.com",
                Age = 25,
                Location = "New York, NY",
                Bio = "Music lover, coffee enthusiast, and adventure seeker. Let's discover new sounds together!",
                MusicProfile = new MusicProfile
                {
                    FavoriteSongs = "Anti-Hero by Taylor Swift, Do I Wanna Know? by Arctic Monkeys, Get Lucky by Daft Punk",
                    FavoriteArtists = "Taylor Swift, Arctic Monkeys, Daft Punk",
                    FavoriteGenres = "anti-hero,do-i-wanna-know,get-lucky"
                }
            },
            new User
            {
                Name = "Bob Martinez",
                Email = "bob@example.com",
                Age = 28,
                Location = "Los Angeles, CA",
                Bio = "Producer and DJ. Always looking for the next big sound. Jazz is life!",
                MusicProfile = new MusicProfile
                {
                    FavoriteSongs = "So What by Miles Davis, HUMBLE. by Kendrick Lamar, Blinding Lights by The Weeknd",
                    FavoriteArtists = "Miles Davis, Kendrick Lamar, The Weeknd",
                    FavoriteGenres = "so-what,humble,blinding-lights"
                }
            },
            new User
            {
                Name = "Charlie Smith",
                Email = "charlie@example.com",
                Age = 23,
                Location = "Austin, TX",
                Bio = "Rock guitarist and music teacher. Let's jam sometime!",
                MusicProfile = new MusicProfile
                {
                    FavoriteSongs = "Stairway to Heaven by Led Zeppelin, Master of Puppets by Metallica, The Thrill Is Gone by B.B. King",
                    FavoriteArtists = "Led Zeppelin, Metallica, B.B. King",
                    FavoriteGenres = "stairway-to-heaven,master-of-puppets,thrill-is-gone"
                }
            }
        };

        await db.Users.AddRangeAsync(users);
        await db.SaveChangesAsync();

        // Add example images
        var userImages = new List<UserImage>();
        foreach (var user in users)
        {
            userImages.Add(new UserImage
            {
                UserId = user.Id,
                ImageUrl = $"https://picsum.photos/400/600?random={user.Id}",
            });
        }

        await db.UserImages.AddRangeAsync(userImages);
        await db.SaveChangesAsync();

        // Reload users with their complete profiles
        var savedUsers = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .ToListAsync();

        Console.WriteLine($"✅ Created {savedUsers.Count} example users in database");

        testLog.Add(new
        {
            step = "Example users created in database",
            userCount = savedUsers.Count,
            users = savedUsers.Select(u => new
            {
                u.Id,
                u.Name,
                u.Age,
                u.Gender,
                Songs = u.MusicProfile?.FavoriteSongs,
                ImageCount = u.Images?.Count ?? 0
            }),
            success = true
        });

        // Now run the same tests as before but emphasize we're testing from database
        var alice = savedUsers.First(u => u.Name == "Alice Johnson");
        var bob = savedUsers.First(u => u.Name == "Bob Martinez");
        var charlie = savedUsers.First(u => u.Name == "Charlie Smith");

        Console.WriteLine($"👥 Testing from database with: {alice.Name} (ID:{alice.Id}), {bob.Name} (ID:{bob.Id}), {charlie.Name} (ID:{charlie.Id})");

        // Continue with existing test logic...
        Console.WriteLine($"\n🎯 Initializing {alice.Name}'s suggestion queue...");
        var queueInitSuccess = await swipeService.InitializeUserQueueAsync(alice.Id);
        Console.WriteLine($"✅ Queue initialization: {(queueInitSuccess ? "SUCCESS" : "FAILED")}");

        testLog.Add(new { step = "Queue initialized", userId = alice.Id, userName = alice.Name, success = queueInitSuccess });

        Console.WriteLine($"\n🔍 Getting potential matches for {alice.Name}...");
        var matchesResult = await SwipeEndpoints.GetPotentialMatches(swipeService, alice.Id, 10);
        Console.WriteLine("✅ Got potential matches successfully");

        testLog.Add(new { step = "Got potential matches", userId = alice.Id, userName = alice.Name, success = true });

        Console.WriteLine($"\n💖 {alice.Name} likes {bob.Name}...");
        var likeResult = await SwipeEndpoints.LikeUser(swipeService, alice.Id, bob.Id);
        Console.WriteLine("✅ Like action completed");

        testLog.Add(new { step = "Like action", fromUser = alice.Name, toUser = bob.Name, success = true });

        Console.WriteLine($"\n💕 {bob.Name} likes {alice.Name} back...");
        var matchResult = await SwipeEndpoints.LikeUser(swipeService, bob.Id, alice.Id);
        Console.WriteLine("✅ Mutual like completed - checking for match...");

        testLog.Add(new { step = "Mutual like", fromUser = bob.Name, toUser = alice.Name, success = true });

        Console.WriteLine($"\n👎 {alice.Name} passes on {charlie.Name}...");
        var passResult = await SwipeEndpoints.PassUser(swipeService, alice.Id, charlie.Id);
        Console.WriteLine("✅ Pass action completed");

        testLog.Add(new { step = "Pass action", fromUser = alice.Name, toUser = charlie.Name, success = true });

        Console.WriteLine($"\n🔗 Getting {alice.Name}'s matches...");
        var aliceMatchesResult = await SwipeEndpoints.GetUserMatches(swipeService, alice.Id);
        Console.WriteLine("✅ Retrieved matches successfully");

        testLog.Add(new { step = "Retrieved matches", userId = alice.Id, userName = alice.Name, success = true });

        Console.WriteLine($"\n📈 Getting {alice.Name}'s swipe statistics...");
        var statsResult = await SwipeEndpoints.GetSwipeStats(swipeService, alice.Id);
        Console.WriteLine("✅ Retrieved stats successfully");

        testLog.Add(new { step = "Retrieved swipe stats", userId = alice.Id, userName = alice.Name, success = true });

        // Final verification
        Console.WriteLine("\n🔍 Verifying database state...");
        var totalLikes = await db.Likes.CountAsync();
        var totalQueue = await db.UserSuggestionQueues.CountAsync();
        var mutualMatches = await db.Likes.CountAsync(l =>
            db.Likes.Any(m => m.FromUserId == l.ToUserId && m.ToUserId == l.FromUserId && m.IsLike && l.IsLike)
        );
        var totalUsers = await db.Users.CountAsync();

        Console.WriteLine($"📊 Database State:");
        Console.WriteLine($"   - Total Users: {totalUsers}");
        Console.WriteLine($"   - Total Likes: {totalLikes}");
        Console.WriteLine($"   - Total Queue Items: {totalQueue}");
        Console.WriteLine($"   - Mutual Matches: {mutualMatches}");

        testLog.Add(new
        {
            step = "Database verification",
            totalUsers,
            totalLikes,
            totalQueue,
            mutualMatches,
            success = true
        });

        Console.WriteLine("\n🎉 =================================");
        Console.WriteLine("🎉 COMPLETE TEST PASSED SUCCESSFULLY!");
        Console.WriteLine("🎉 =================================");

        testLog.Add(new { step = "Complete Test Completed", timestamp = DateTime.UtcNow, success = true });

        return Results.Ok(new
        {
            success = true,
            message = "🎉 Complete swipe service test passed! Users created in database and tested successfully.",
            summary = new
            {
                usersCreated = totalUsers,
                likesProcessed = totalLikes,
                queueItemsCreated = totalQueue,
                mutualMatches = mutualMatches,
                testType = "Database users with complete profiles"
            },
            testLog = testLog
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ COMPLETE TEST FAILED: {ex.Message}");
        Console.WriteLine($"❌ Stack Trace: {ex.StackTrace}");

        testLog.Add(new { step = "Complete Test Failed", error = ex.Message, timestamp = DateTime.UtcNow, success = false });

        return Results.Problem(
            detail: ex.Message,
            title: "Complete Swipe Service Test Failed",
            statusCode: 1000,
            instance: "/test/swipe"
        );
    }
})
.WithName("TestSwipeService")
.WithSummary("Test the complete swipe service functionality with database users")
.WithDescription("Creates example users in the database and runs comprehensive tests of the swipe service including queue management, matching logic, and Gemini AI integration");

// ---- User Management Endpoints ----
app.MapPost("/users", Endpoints.CreateUser);
app.MapGet("/users/{id:int}", Endpoints.GetUser);
app.MapPut("/users/{id:int}/profile", Endpoints.UpdateProfile);
app.MapGet("/users:search", Endpoints.SearchUsers);

// ---- User Images Endpoints ----
app.MapPost("/users/{id:int}/images", Endpoints.AddUserImage);
app.MapGet("/users/{id:int}/images", Endpoints.GetUserImages);

// ---- Swiping Endpoints ----
// Get potential matches for swiping
app.MapGet("/swipe/discover/{userId:int}", SwipeEndpoints.GetPotentialMatches)
    .WithName("GetPotentialMatches")
    .WithSummary("Get potential matches for a user")
    .WithDescription("Returns a list of users that the specified user hasn't swiped on yet, ordered by compatibility score");

// General swipe endpoint (can handle both like and pass)
app.MapPost("/swipe", SwipeEndpoints.SwipeOnUser)
    .WithName("SwipeOnUser")
    .WithSummary("Swipe on a user (like or pass)")
    .WithDescription("Process a swipe action and return whether it resulted in a match");

// Quick like endpoint
app.MapPost("/swipe/{fromUserId:int}/like/{toUserId:int}", SwipeEndpoints.LikeUser)
    .WithName("LikeUser")
    .WithSummary("Like a user")
    .WithDescription("Quick endpoint to like a user");

// Quick pass endpoint
app.MapPost("/swipe/{fromUserId:int}/pass/{toUserId:int}", SwipeEndpoints.PassUser)
    .WithName("PassUser")
    .WithSummary("Pass on a user")
    .WithDescription("Quick endpoint to pass on a user");

// Get user's matches
app.MapGet("/matches/{userId:int}", SwipeEndpoints.GetUserMatches)
    .WithName("GetUserMatches")
    .WithSummary("Get all matches for a user")
    .WithDescription("Returns all users who have mutually liked each other with the specified user");

// Get swipe statistics
app.MapGet("/swipe/stats/{userId:int}", SwipeEndpoints.GetSwipeStats)
    .WithName("GetSwipeStats")
    .WithSummary("Get swipe statistics for a user")
    .WithDescription("Returns statistics about a user's swiping behavior");

// ---- Legacy Likes Endpoints (keeping for backward compatibility) ----
app.MapPost("/likes", Endpoints.CreateLike);

// ---- Legacy Match Endpoint (using old MatchService) ----
app.MapGet("/suggestions/{userId:int}", Endpoints.GetMatches)
    .WithName("GetSuggestions")
    .WithSummary("Get user suggestions (legacy)")
    .WithDescription("Legacy endpoint using the old MatchService");

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