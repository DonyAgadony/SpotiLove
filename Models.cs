using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore; // Required for LINQ usage in DtoMappers

namespace Spotilove;

// =======================================================
// ===== DATABASE ENTITIES (MODELS) ======================
// =======================================================

/// <summary>
/// Core User Entity (Stored in the database)
/// </summary>
public class User
{
    // Primary Key
    [Key]
    public int Id { get; set; }

    // Core Profile Data
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Range(18, 120)]
    public int Age { get; set; }

    [Required, MaxLength(20)]
    public string Gender { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Bio { get; set; }

    [MaxLength(100)]
    public string? Location { get; set; }

    // Authentication & Auditing
    [Required, EmailAddress, MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [JsonIgnore] // Typically ignore the hash for API responses
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- Navigation Properties ---

    // 1-1 relationship with MusicProfile
    public MusicProfile? MusicProfile { get; set; }

    // 1-Many relationship with UserImage
    public List<UserImage> Images { get; set; } = new();

    // Many-to-Many relationship via Like (Outgoing swipes/likes INITIATED by this user)
    // Used by AppDbContext for Likes.FromUser relationship
    public List<Like> Likes { get; set; } = new();

    // Many-to-Many relationship via Like (Incoming swipes/likes RECEIVED by this user)
    // Used by AppDbContext for Likes.ToUser relationship
    public List<Like> LikesReceived { get; set; } = new();

    // 1-Many relationship with UserSuggestionQueue (This user's queue of potential matches)
    // Used by AppDbContext for USQ.User relationship
    public List<UserSuggestionQueue> Suggestions { get; set; } = new();
}

/// <summary>
/// Stores music-related preferences for a User (1-1 relationship).
/// </summary>
public class MusicProfile
{
    // Primary Key (often same as UserId for a 1:1 relationship)
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; } // Foreign Key to User

    // Music Data (Stored as strings, often JSON or comma-separated in real apps)
    [MaxLength(500)]
    public string FavoriteGenres { get; set; } = "";

    [MaxLength(1000)]
    public string FavoriteArtists { get; set; } = "";

    [MaxLength(1000)]
    public string FavoriteSongs { get; set; } = "";

    // Navigation Property
    [JsonIgnore]
    public User? User { get; set; }
}

/// <summary>
/// Represents a URL for a user's profile picture (1-Many relationship).
/// </summary>
public class UserImage
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [Required, MaxLength(2048)]
    // FIX: Renamed 'Url' back to 'ImageUrl' to resolve external CS1061 errors
    public string ImageUrl { get; set; } = string.Empty;

    // Provides 'Url' as a simple alias for backwards compatibility in external code
    [NotMapped]
    public string Url { get => ImageUrl; set => ImageUrl = value; }

    // Navigation Property
    [JsonIgnore]
    public User? User { get; set; }
}

/// <summary>
/// Represents a swipe/like action between two users (Many-to-Many relationship).
/// Uses a composite key (FromUserId, ToUserId).
/// </summary>
public class Like
{
    // Composite Key Part 1: The user who initiated the swipe
    [Key, Column(Order = 0)]
    public int FromUserId { get; set; }

    // Composite Key Part 2: The user who was swiped on
    [Key, Column(Order = 1)]
    public int ToUserId { get; set; }

    // True for like (swipe right), false for dislike (swipe left)
    public bool IsLike { get; set; }

    // True if the ToUser has also liked the FromUser (resulting in a match)
    public bool IsMatch { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    [JsonIgnore]
    public User FromUser { get; set; } = null!;

    [JsonIgnore]
    public User ToUser { get; set; } = null!;
}

/// <summary>
/// Represents a user suggested as a potential match, stored in a queue.
/// Uses a composite key (UserId, SuggestedUserId).
/// </summary>
public class UserSuggestionQueue
{
    // Composite Key Part 1: The ID of the user whose queue this item belongs to
    [Key, Column(Order = 0)]
    public int UserId { get; set; }

    // Composite Key Part 2: The ID of the user being suggested
    [Key, Column(Order = 1)]
    public int SuggestedUserId { get; set; }

    public double CompatibilityScore { get; set; }
    public int QueuePosition { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    [JsonIgnore]
    public User User { get; set; } = null!;

    [JsonIgnore]
    public User SuggestedUser { get; set; } = null!;
}

// =======================================================
// ===== DTOs (Data Transfer Objects) and Requests =======
// =======================================================

public class MusicProfileDto
{
    public string FavoriteSongs { get; set; } = string.Empty;
    public string FavoriteArtists { get; set; } = string.Empty;
    public string FavoriteGenres { get; set; } = string.Empty;
}

public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string? Location { get; set; }
    public string? Bio { get; set; }
    public string? Gender { get; set; }

    public string? Email { get; set; }

    public MusicProfileDto? MusicProfile { get; set; }

    // List of Image URLs
    public List<string> Images { get; set; } = new List<string>();
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
    [Required] public string Name { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    [Range(18, 120)] public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
}

public class LoginRequestFromApp
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}


public class CompatibilityResult : UserDto // Inheriting from UserDto is cleaner
{
    public double CompatibilityScore { get; set; }
}

// ===== REQUEST/RECORDS (Simplified DTOs for Endpoints) =====
public record CreateUserDto(
    string Name,
    int Age,
    string Gender,
    string Email,
    string Password,
    string Genres,
    string Artists,
    string Songs = "");

// Changed to nullable strings for partial updates
public record UpdateProfileDto(string? Genres, string? Artists, string? Songs);

public record LikeDto(int FromUserId, int ToUserId, bool IsLike);

// FIX: Added missing DTO for batch processing service
public record BatchCalculateRequest(
    int UserId,
    List<int> TargetUserIds
);

public class ResponseMessage
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}

// =======================================================
// ===== DTO MAPPERS (To resolve CS8801 error in Database.cs) =====
// =======================================================
public static class DtoMappers
{
    /// <summary>
    /// Helper to convert User entity to UserDto
    /// </summary>
    public static UserDto ToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Age = user.Age,
            Email = user.Email,
            Location = user.Location,
            Bio = user.Bio,
            Gender = user.Gender,
            // FIX: Using the ImageUrl property
            Images = user.Images.Select(i => i.ImageUrl).ToList(),
            MusicProfile = user.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = user.MusicProfile.FavoriteArtists,
                FavoriteGenres = user.MusicProfile.FavoriteGenres,
                FavoriteSongs = user.MusicProfile.FavoriteSongs,
            } : null
        };
    }
}
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

        user.MusicProfile.FavoriteGenres = dto.Genres!;
        user.MusicProfile.FavoriteArtists = dto.Artists!;
        user.MusicProfile.FavoriteSongs = dto.Songs!;

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