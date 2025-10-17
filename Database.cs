using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public class AppDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MusicProfile> MusicProfiles => Set<MusicProfile>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<UserImage> UserImages { get; set; }
    public DbSet<UserSuggestionQueue> UserSuggestionQueues { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure User-MusicProfile relationship (one-to-one)
        modelBuilder.Entity<User>()
            .HasOne(u => u.MusicProfile)
            .WithOne(m => m.User)
            .HasForeignKey<MusicProfile>(m => m.UserId);

        // Configure User-UserImage relationship (one-to-many)
        modelBuilder.Entity<UserImage>()
            .HasOne(ui => ui.User)
            .WithMany(u => u.Images)
            .HasForeignKey(ui => ui.UserId);

        // Configure Like relationships
        modelBuilder.Entity<Like>()
            .HasOne(l => l.FromUser)
            .WithMany()
            .HasForeignKey(l => l.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Like>()
            .HasOne(l => l.ToUser)
            .WithMany()
            .HasForeignKey(l => l.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Add index for better query performance on likes
        modelBuilder.Entity<Like>()
            .HasIndex(l => new { l.FromUserId, l.ToUserId })
            .IsUnique();

        // Configure UserSuggestionQueue relationships
        modelBuilder.Entity<UserSuggestionQueue>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SuggestedUser)
                  .WithMany()
                  .HasForeignKey(e => e.SuggestedUserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.UserId, e.QueuePosition });
            entity.HasIndex(e => new { e.UserId, e.SuggestedUserId })
                  .IsUnique();
        });
    }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;

    // Authentication fields
    public string? Email { get; set; }
    public string? PasswordHash { get; set; } // Store hashed password
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Profile fields
    public string? Bio { get; set; }
    public string? Location { get; set; }

    // Navigation properties
    public MusicProfile? MusicProfile { get; set; }
    public List<UserImage> Images { get; set; } = new();
    public List<Like> Likes { get; set; } = new();
}

// Add password hashing utility class
public static class PasswordHasher
{
    // Simple hash for demo - use BCrypt or ASP.NET Identity in production
    public static string HashPassword(string password)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password + "SpotiLove_Salt");
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        var newHash = HashPassword(password);
        return newHash == hash;
    }
}
public class RegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
}
public class LoginRequestFromApp
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class UserImage
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int UserId { get; set; }
    public User? User { get; set; }
    public string? ImageUrl { get; set; }
}

public class MusicProfile
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string FavoriteGenres { get; set; } = "";
    public string FavoriteArtists { get; set; } = "";
    public string FavoriteSongs { get; set; } = "";
    public User? User { get; set; }
}
public class MusicProfileDto
{
    public string? FavoriteSongs { get; set; }
    public string? FavoriteArtists { get; set; }
    public string? FavoriteGenres { get; set; }
}

public class UserImageDto
{
    public string? ImageUrl { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public int Age { get; set; }
    public string? Location { get; set; }
    public string? Bio { get; set; }
    public MusicProfileDto? MusicProfile { get; set; }
    public List<string>? Images { get; set; }
}

public class TakeExUsersResponse
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public List<UserDto> Users { get; set; } = new();
}
public class ResponseMessage
{
    public bool Success { get; set; }
}

public class Like
{
    public int Id { get; set; }
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public bool IsLike { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public User? FromUser { get; set; }
    public User? ToUser { get; set; }
}

// New Entity for Queue Management
public class UserSuggestionQueue
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SuggestedUserId { get; set; }
    public int QueuePosition { get; set; }
    public DateTime CreatedAt { get; set; }
    public double CompatibilityScore { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public User SuggestedUser { get; set; } = null!;
}

// DTOs
public record CreateUserDto(string Name, int Age, string Gender, string Genres, string Artists, string Songs = "");
public record UpdateProfileDto(string Genres, string Artists, string Songs = "");
public record LikeDto(int FromUserId, int ToUserId, bool IsLike);

// Endpoints
public static class Endpoints
{
    // User endpoints
    public static async Task<IResult> CreateUser(AppDbContext db, CreateUserDto dto)
    {
        var user = new User
        {
            Name = dto.Name,
            Age = dto.Age,
            Gender = dto.Gender,
            MusicProfile = new MusicProfile
            {
                FavoriteGenres = dto.Genres,
                FavoriteArtists = dto.Artists,
                FavoriteSongs = dto.Songs
            }
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Results.Ok(user);
    }

    public static async Task<IResult> GetUser(AppDbContext db, int id)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        return Results.Ok(user);
    }

    public static async Task<IResult> UpdateProfile(AppDbContext db, int id, UpdateProfileDto dto)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        if (user.MusicProfile == null) return Results.BadRequest("User has no music profile");

        user.MusicProfile.FavoriteGenres = dto.Genres;
        user.MusicProfile.FavoriteArtists = dto.Artists;
        user.MusicProfile.FavoriteSongs = dto.Songs;

        await db.SaveChangesAsync();
        return Results.Ok(user);
    }

    public static async Task<IResult> SearchUsers(AppDbContext db)
    {
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .ToListAsync();

        return Results.Ok(users);
    }

    // Swipe endpoints (updated to use SwipeService)
    public static async Task<IResult> SwipeUser(AppDbContext db, SwipeService swipeService, SwipeDto dto)
    {
        try
        {
            var result = await swipeService.SwipeAsync(dto.FromUserId, dto.ToUserId, dto.IsLike);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    public static async Task<IResult> GetPotentialMatches(AppDbContext db, SwipeService swipeService, int userId, int count = 10)
    {
        var suggestions = await swipeService.GetPotentialMatchesAsync(userId, count);
        return Results.Ok(suggestions);
    }

    public static async Task<IResult> GetMatches(AppDbContext db, SwipeService swipeService, int userId)
    {
        var matches = await swipeService.GetMatchesAsync(userId);
        return Results.Ok(matches);
    }

    public static async Task<IResult> GetSwipeStats(AppDbContext db, SwipeService swipeService, int userId)
    {
        var stats = await swipeService.GetSwipeStatsAsync(userId);
        return Results.Ok(stats);
    }

    public static async Task<IResult> InitializeUserQueue(AppDbContext db, SwipeService swipeService, int userId)
    {
        var success = await swipeService.InitializeUserQueueAsync(userId);
        if (success)
        {
            return Results.Ok(new { Message = "Queue initialized successfully" });
        }
        return Results.BadRequest("Failed to initialize queue - user may not exist or have no music profile");
    }

    public static async Task<IResult> GetQueueStatus(AppDbContext db, SwipeService swipeService, int userId)
    {
        var status = await swipeService.GetQueueStatusAsync(userId);
        return Results.Ok(status);
    }

    // Like endpoints (legacy - for backward compatibility)
    public static async Task<IResult> CreateLike(AppDbContext db, LikeDto dto)
    {
        var like = new Like
        {
            FromUserId = dto.FromUserId,
            ToUserId = dto.ToUserId,
            IsLike = dto.IsLike
        };

        db.Likes.Add(like);
        await db.SaveChangesAsync();

        // Check for mutual match
        bool mutual = dto.IsLike && await db.Likes.AnyAsync(l =>
            l.FromUserId == dto.ToUserId &&
            l.ToUserId == dto.FromUserId &&
            l.IsLike);

        return Results.Ok(new { like.Id, MutualMatch = mutual });
    }

    // User Image endpoints
    public static async Task<IResult> AddUserImage(AppDbContext db, int id, UserImage image)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return Results.NotFound("User not found");

        image.UserId = id;
        db.UserImages.Add(image);
        await db.SaveChangesAsync();

        return Results.Ok(image);
    }

    public static async Task<IResult> GetUserImages(AppDbContext db, int id)
    {
        var user = await db.Users
            .Include(u => u.Images)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        return Results.Ok(user.Images);
    }
}

// Legacy MatchService (kept for backward compatibility)
public class MatchService
{
    private readonly AppDbContext _db;

    public MatchService(AppDbContext db) => _db = db;

    public async Task<IEnumerable<object>> GetSuggestionsAsync(int userId)
    {
        var user = await _db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user?.MusicProfile == null) return Enumerable.Empty<object>();

        // Split into arrays
        var myGenres = user.MusicProfile.FavoriteGenres
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var myArtists = user.MusicProfile.FavoriteArtists
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var others = await _db.Users
            .Include(u => u.MusicProfile)
            .Where(u => u.Id != userId && u.MusicProfile != null)
            .ToListAsync();

        return others.Select(o =>
        {
            var genres = o.MusicProfile!.FavoriteGenres
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var artists = o.MusicProfile.FavoriteArtists
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            double genreSim = Jaccard(myGenres, genres);
            double artistSim = Jaccard(myArtists, artists);
            double score = genreSim * 0.7 + artistSim * 0.3;

            return new { o.Id, o.Name, o.Age, o.Gender, Score = Math.Round(score, 3) };
        })
        .OrderByDescending(x => x.Score)
        .Take(10);
    }

    private static double Jaccard(string[] a, string[] b)
    {
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }
}

public static class DatabaseHelper
{
    public static async Task<User> AddUserAsync(AppDbContext db, string name, int age, string gender, string genres, string artists, string songs = "")
    {
        var user = new User
        {
            Name = name,
            Age = age,
            Gender = gender,
            MusicProfile = new MusicProfile
            {
                FavoriteGenres = genres,
                FavoriteArtists = artists,
                FavoriteSongs = songs
            }
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task SeedDatabaseAsync(AppDbContext db)
    {
        // Only seed if database is empty
        if (await db.Users.AnyAsync()) return;

        var users = new[]
        {
            new { Name = "Alice", Age = 25, Gender = "Female", Genres = "Pop,Rock,Indie", Artists = "Taylor Swift,Arctic Monkeys,Billie Eilish", Songs = "Anti-Hero,Do I Wanna Know,Bad Guy" },
            new { Name = "Bob", Age = 28, Gender = "Male", Genres = "Hip Hop,R&B,Jazz", Artists = "Kendrick Lamar,The Weeknd,Miles Davis", Songs = "HUMBLE,Blinding Lights,Kind of Blue" },
            new { Name = "Charlie", Age = 24, Gender = "Non-binary", Genres = "Electronic,Ambient,Techno", Artists = "Aphex Twin,Boards of Canada,Daft Punk", Songs = "Windowlicker,Roygbiv,One More Time" },
            new { Name = "Diana", Age = 30, Gender = "Female", Genres = "Classical,Opera,Chamber", Artists = "Mozart,Maria Callas,Yo-Yo Ma", Songs = "Eine kleine Nachtmusik,La Traviata,Bach Cello Suite" },
            new { Name = "Ethan", Age = 26, Gender = "Male", Genres = "Rock,Metal,Alternative", Artists = "Metallica,Radiohead,Foo Fighters", Songs = "Enter Sandman,Creep,Everlong" }
        };

        foreach (var userData in users)
        {
            await AddUserAsync(db, userData.Name, userData.Age, userData.Gender, userData.Genres, userData.Artists, userData.Songs);
        }

        Console.WriteLine("Database seeded with sample users");
    }
}