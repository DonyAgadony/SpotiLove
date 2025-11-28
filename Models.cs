using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public string? Location { get; set; }
    public string Email { get; set; } = string.Empty;
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    public string? SexualOrientation { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public MusicProfile? MusicProfile { get; set; }
    public List<UserImage> Images { get; set; } = new();
    public List<Like> LikesSent { get; set; } = new();
    public List<Like> LikesReceived { get; set; } = new();
    public List<UserSuggestionQueue> Suggestions { get; set; } = new();
}

public class MusicProfile
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public List<string> FavoriteSongs { get; set; } = new();

    [JsonIgnore]
    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }
}

public class UserImage
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string ImageUrl { get; set; } = string.Empty;

    [NotMapped]
    public string Url
    {
        get => ImageUrl;
        set => ImageUrl = value;
    }

    [JsonIgnore]
    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }
}

public class Like
{
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public bool IsLike { get; set; }
    public bool IsMatch { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    [ForeignKey(nameof(FromUserId))]
    public User FromUser { get; set; } = null!;

    [JsonIgnore]
    [ForeignKey(nameof(ToUserId))]
    public User ToUser { get; set; } = null!;
}

public class UserSuggestionQueue
{
    public int UserId { get; set; }
    public int SuggestedUserId { get; set; }
    public double CompatibilityScore { get; set; }
    public int QueuePosition { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    [JsonIgnore]
    [ForeignKey(nameof(SuggestedUserId))]
    public User SuggestedUser { get; set; } = null!;
}

// =======================================================
// ===== DTOs (Data Transfer Objects) and Requests =======
// =======================================================
public record SendLikeDto(int FromUserId, int ToUserId);

public class MusicProfileDto
{
    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public List<string> FavoriteSongs { get; set; } = new();
}

public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Age { get; set; }
    public string Location { get; set; } = "";
    public string Bio { get; set; } = "";
    public string Gender { get; set; } = "";
    public string? SexualOrientation { get; set; }
    public MusicProfileDto MusicProfile { get; set; } = new();
    public List<string> Images { get; set; } = new();
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
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Range(18, 120)]
    public int Age { get; set; }

    public string Gender { get; set; } = string.Empty;

    public string? SexualOrientation { get; set; }

    public string? Bio { get; set; } // Add Bio field

    public string? ProfileImage { get; set; } // Profile image as base64
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
    string? ProfileImage = null,
    string Songs = "",
    string? SexualOrientation = null,
    string? GenderIdentity = null,
    string? AttractionPreferences = null
);
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

    /// Helper to convert User entity to UserDto

    public static UserDto ToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Age = user.Age,
            Email = user.Email,
            Location = user.Location!,
            Bio = user.Bio!,
            Gender = user.Gender,
            Images = user.Images.Select(i => i.ImageUrl).ToList(),
            MusicProfile = user.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = user.MusicProfile.FavoriteArtists!,
                FavoriteGenres = user.MusicProfile.FavoriteGenres!,
                FavoriteSongs = user.MusicProfile.FavoriteSongs!,
            } : null
        };
    }
}
public static class Endpoints
{

    /// Creates a new user with an initial music profile.

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
                FavoriteGenres = dto.Genres.Split(',').ToList(),
                FavoriteArtists = dto.Artists.Split(',').ToList(),
                FavoriteSongs = dto.Songs.Split(',').ToList()
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


    /// Gets a user profile by ID.

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


    /// Updates only the music profile fields.

    public static async Task<IResult> UpdateProfile(AppDbContext db, int id, UpdateProfileDto dto)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        if (user.MusicProfile == null) return Results.BadRequest("User has no music profile");

        user.MusicProfile.FavoriteGenres = dto.Genres!.Split(',').ToList();
        user.MusicProfile.FavoriteArtists = dto.Artists!.Split(',').ToList();
        user.MusicProfile.FavoriteSongs = dto.Songs!.Split(',').ToList();

        await db.SaveChangesAsync();
        return Results.Ok(user);
    }


    /// Returns a list of all users.

    public static async Task<IResult> SearchUsers(AppDbContext db)
    {
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .ToListAsync();

        return Results.Ok(users);
    }


    /// Adds a new image URL to a user's profile.

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


    /// Gets all images for a specific user.

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