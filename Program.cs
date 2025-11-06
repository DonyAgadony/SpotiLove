using Microsoft.EntityFrameworkCore;
using Spotilove;
using DotNetEnv;
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
    if (connectionString.StartsWith("postgres://"))
    {
        // Convert Render-style postgres:// URL to standard connection string
        var databaseUri = new Uri(connectionString);
        var userInfo = databaseUri.UserInfo.Split(':');

        var connStr = $"Host={databaseUri.Host};" +
                     $"Port={databaseUri.Port};" +
                     $"Database={databaseUri.LocalPath.TrimStart('/')};" +
                     $"Username={userInfo[0]};" +
                     $"Password={userInfo[1]};" +
                     $"SSL Mode=Require;" +
                     $"Trust Server Certificate=true";

        opt.UseNpgsql(connStr);
        Console.WriteLine("🐘 Using PostgreSQL database");
    }
    else
    {
        opt.UseSqlite(connectionString);
        Console.WriteLine("📁 Using SQLite database (local dev)");
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

// Debug endpoint
app.MapGet("/debug/user/{id:int}", async (AppDbContext db, int id) =>
{
    var user = await db.Users.Include(u => u.MusicProfile).FirstOrDefaultAsync(u => u.Id == id);
    if (user == null)
        return Results.Ok(new { exists = false, message = $"User {id} not found" });

    return Results.Ok(new
    {
        exists = true,
        user = new
        {
            user.Id,
            user.Name,
            user.Email,
            hasMusicProfile = user.MusicProfile != null,
            musicProfile = user.MusicProfile != null ? new
            {
                genres = user.MusicProfile.FavoriteGenres,
                artists = user.MusicProfile.FavoriteArtists,
                songs = user.MusicProfile.FavoriteSongs
            } : null
        }
    });
});

// ===========================================================
// 🎧 FIX USER 101 MUSIC PROFILE (one-time command)
// ===========================================================
app.MapPost("/fix-user101", async (AppDbContext db) =>
{
    var user = await db.Users.Include(u => u.MusicProfile).FirstOrDefaultAsync(u => u.Id == 101);

    if (user == null)
        return Results.NotFound("❌ User 101 not found");

    if (user.MusicProfile == null)
    {
        user.MusicProfile = new MusicProfile
        {
            UserId = 101,
            FavoriteGenres = "Pop, Indie, R&B, Soul",
            FavoriteArtists = "Adele, Ed Sheeran, The Weeknd, Billie Eilish, Coldplay",
            FavoriteSongs = "Someone Like You, Shape of You, Blinding Lights, Fix You, Ocean Eyes"
        };
        db.MusicProfiles.Add(user.MusicProfile);
    }
    else
    {
        user.MusicProfile.FavoriteGenres = "Pop, Indie, R&B, Soul";
        user.MusicProfile.FavoriteArtists = "Adele, Ed Sheeran, The Weeknd, Billie Eilish, Coldplay";
        user.MusicProfile.FavoriteSongs = "Someone Like You, Shape of You, Blinding Lights, Fix You, Ocean Eyes";
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { success = true, message = "🎧 Updated user 101 with a real music profile" });
});

// ===========================================================
// (Keep the rest of your endpoints exactly as-is below this)
// ===========================================================

// ... keep all your other /users, /music-profile, /auth, /swipe, etc. endpoints ...

Console.WriteLine("🎯 Spotilove API is starting...");
Console.WriteLine($"🌐 Running on port: {port}");
Console.WriteLine("🤖 Gemini AI compatibility calculation enabled");
Console.WriteLine("⚙️  Make sure to set GeminiAPIKey environment variable for AI features");
Console.WriteLine("🧪 Test endpoints available:");
Console.WriteLine("   - POST /fix-user101");
Console.WriteLine("   - POST /seed-database");
Console.WriteLine($"📖 View API documentation at: http://0.0.0.0:{port}/swagger");

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

        users.Add(new User
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
        });
    }

    await db.Users.AddRangeAsync(users);
    await db.SaveChangesAsync();

    var userImages = users.SelectMany(user => new[]
    {
        new UserImage { UserId = user.Id, ImageUrl = $"https://picsum.photos/400/600?random={user.Id}1" },
        new UserImage { UserId = user.Id, ImageUrl = $"https://picsum.photos/400/600?random={user.Id}2" }
    }).ToList();

    await db.UserImages.AddRangeAsync(userImages);
    await db.SaveChangesAsync();

    Console.WriteLine($"✅ Seeded database with {users.Count} users");
}
