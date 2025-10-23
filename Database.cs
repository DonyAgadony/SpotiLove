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
        // ===== User ↔ MusicProfile (1-1) =====
        modelBuilder.Entity<User>()
            .HasOne(u => u.MusicProfile)
            .WithOne(m => m.User)
            .HasForeignKey<MusicProfile>(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ===== User ↔ UserImage (1-many) =====
        modelBuilder.Entity<UserImage>()
            .HasOne(ui => ui.User)
            .WithMany(u => u.Images)
            .HasForeignKey(ui => ui.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ===== Like Relationships =====
        modelBuilder.Entity<Like>()
            .HasOne(l => l.FromUser)
            .WithMany(u => u.Likes)
            .HasForeignKey(l => l.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Like>()
            .HasOne(l => l.ToUser)
            .WithMany()
            .HasForeignKey(l => l.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // OPTIMIZATION: Composite unique index (prevents duplicates + faster queries)
        modelBuilder.Entity<Like>()
            .HasIndex(l => new { l.FromUserId, l.ToUserId })
            .IsUnique();

        // ===== UserSuggestionQueue Configuration =====
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

            // OPTIMIZATION: Composite index for queue position lookups
            entity.HasIndex(e => new { e.UserId, e.QueuePosition });

            // OPTIMIZATION: Unique constraint to prevent duplicate suggestions
            entity.HasIndex(e => new { e.UserId, e.SuggestedUserId })
                  .IsUnique();

            // OPTIMIZATION: Index for score-based queries
            entity.HasIndex(e => new { e.UserId, e.CompatibilityScore });
        });

        // OPTIMIZATION: Index on Email for auth lookups
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email);
    }
}

// ===== ENTITIES =====
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;

    // Authentication
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Profile
    public string? Bio { get; set; }
    public string? Location { get; set; }

    // Navigation
    public MusicProfile? MusicProfile { get; set; }
    public List<UserImage> Images { get; set; } = new();
    public List<Like> Likes { get; set; } = new();
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

public class UserImage
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int UserId { get; set; }
    public User? User { get; set; }
    public string? ImageUrl { get; set; }
}

public class Like
{
    public int Id { get; set; }
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public bool IsLike { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? FromUser { get; set; }
    public User? ToUser { get; set; }
}

public class UserSuggestionQueue
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SuggestedUserId { get; set; }
    public int QueuePosition { get; set; }
    public double CompatibilityScore { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public User SuggestedUser { get; set; } = null!;
}

// ===== PASSWORD UTILS =====
public static class PasswordHasher
{
    public static string HashPassword(string password)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password + "SpotiLove_Salt");
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        return HashPassword(password) == hash;
    }
}

// ===== DTOs =====
public class MusicProfileDto
{
    public string? FavoriteSongs { get; set; }
    public string? FavoriteArtists { get; set; }
    public string? FavoriteGenres { get; set; }
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
    public string? Message { get; set; }
}

public class RegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public int Id { get; set; }
}

public class LoginRequestFromApp
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
    public int Id { get; set; }
}

public class BatchCalculateRequest
{
    public int CurrentUserId { get; set; }
    public List<int> UserIds { get; set; } = new();
}

public class CompatibilityResult
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string? Location { get; set; }
    public string? Bio { get; set; }
    public double CompatibilityScore { get; set; }
    public MusicProfileDto? MusicProfile { get; set; }
    public List<string> Images { get; set; } = new();
}

// ===== ENDPOINTS =====
public static class Endpoints
{
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
            .AsNoTracking()
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
            .AsNoTracking()
            .ToListAsync();

        return Results.Ok(users);
    }

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
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");

        return Results.Ok(user.Images);
    }
}

// ===== REQUEST/RECORDS =====
public record CreateUserDto(string Name, int Age, string Gender, string Genres, string Artists, string Songs = "");
public record UpdateProfileDto(string Genres, string Artists, string Songs = "");
public record LikeDto(int FromUserId, int ToUserId, bool IsLike);

public class ResponseMessage
{
    public bool Success { get; set; }
}