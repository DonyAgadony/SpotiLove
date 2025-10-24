using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Spotilove;
// =======================================================
// ===== DATABASE CONTEXT =====
// =======================================================
public class AppDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MusicProfile> MusicProfiles => Set<MusicProfile>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<UserImage> UserImages { get; set; } = null!;
    public DbSet<UserSuggestionQueue> UserSuggestionQueues { get; set; } = null!;

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
        // FromUser is the user initiating the like
        modelBuilder.Entity<Like>()
            .HasOne(l => l.FromUser)
            .WithMany(u => u.Swipes)
            .HasForeignKey(l => l.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // ToUser is the user being liked
        modelBuilder.Entity<Like>()
            .HasOne(l => l.ToUser)
            .WithMany() // No navigation property on User for "received likes", but we define the relationship
            .HasForeignKey(l => l.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // OPTIMIZATION: Composite unique index (prevents duplicates + faster queries)
        modelBuilder.Entity<Like>()
            .HasIndex(l => new { l.FromUserId, l.ToUserId })
            .IsUnique();

        // ===== UserSuggestionQueue Configuration =====
        modelBuilder.Entity<UserSuggestionQueue>(entity =>
        {
            entity.HasKey(e => e.UserId);

            entity.HasOne(e => e.User) // The user whose queue this is
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SuggestedUser) // The user being suggested
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

    /// <summary>
    /// Verifies a plain text password against a stored hash.
    /// </summary>
    public static bool VerifyPassword(string password, string hash)
    {
        return HashPassword(password) == hash;
    }
}

// ===== DTOs (Data Transfer Objects) =====
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

// ===== REQUEST/RECORDS (Simplified DTOs for Endpoints) =====
public record CreateUserDto(string Name, int Age, string Gender, string Genres, string Artists, string Songs = "");
public record UpdateProfileDto(string Genres, string Artists, string Songs = "");
public record LikeDto(int FromUserId, int ToUserId, bool IsLike);

public class ResponseMessage
{
    public bool Success { get; set; }
}

// =======================================================
// ===== ENDPOINT HANDLERS (for Minimal API) =====
// =======================================================
public static class Endpoints
{
    /// <summary>
    /// Creates a new user with an initial music profile.
    /// </summary>
    public static async Task<IResult> CreateUser(AppDbContext db, CreateUserDto dto)
    {
        var user = new User
        {
            Name = dto.Name,
            Age = dto.Age,
            Gender = dto.Gender,
            // Note: Email/Password fields are missing here, this is a simplified user creation.
            MusicProfile = new MusicProfile
            {
                FavoriteGenres = dto.Genres,
                FavoriteArtists = dto.Artists,
                FavoriteSongs = dto.Songs
            }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Results.Ok(new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Age = user.Age,
            MusicProfile = user.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = user.MusicProfile.FavoriteArtists,
                FavoriteGenres = user.MusicProfile.FavoriteGenres,
                FavoriteSongs = user.MusicProfile.FavoriteSongs,
            } : null
        });
    }

    /// <summary>
    /// Gets a user profile by ID.
    /// </summary>
    public static async Task<IResult> GetUser(AppDbContext db, int id)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        return Results.Ok(user); // Returning the full entity for simplicity
    }

    /// <summary>
    /// Updates only the music profile fields.
    /// </summary>
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

    /// <summary>
    /// Returns a list of all users.
    /// </summary>
    public static async Task<IResult> SearchUsers(AppDbContext db)
    {
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .ToListAsync();

        return Results.Ok(users);
    }

    /// <summary>
    /// Adds a new image URL to a user's profile.
    /// </summary>
    public static async Task<IResult> AddUserImage(AppDbContext db, int id, UserImage image)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return Results.NotFound("User not found");

        image.UserId = id;
        // Using the ImageUrl property from the input DTO/Entity provided by the user
        if (string.IsNullOrEmpty(image.Url) && !string.IsNullOrEmpty(image.ImageUrl))
        {
            image.Url = image.ImageUrl;
        }

        db.UserImages.Add(image);
        await db.SaveChangesAsync();

        return Results.Ok(image);
    }

    /// <summary>
    /// Gets all images for a specific user.
    /// </summary>
    public static async Task<IResult> GetUserImages(AppDbContext db, int id)
    {
        var user = await db.Users
            .Include(u => u.Images)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");

        return Results.Ok(user.Images.Select(i => i.Url).ToList());
    }
}
