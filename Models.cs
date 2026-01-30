using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public class User
{
    [Key]
    [Column(TypeName = "uuid")]  // ✅ Explicitly set PostgreSQL type
    public Guid Id { get; set; } = Guid.NewGuid();

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
    [Key]
    [Column(TypeName = "uuid")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column(TypeName = "uuid")]
    public Guid UserId { get; set; }

    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public List<string> FavoriteSongs { get; set; } = new();

    [JsonIgnore]
    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }
}

public class UserImage
{
    [Key]
    [Column(TypeName = "uuid")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column(TypeName = "uuid")]
    public Guid UserId { get; set; }

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
    [Key, Column(Order = 0, TypeName = "uuid")]
    public Guid FromUserId { get; set; }

    [Key, Column(Order = 1, TypeName = "uuid")]
    public Guid ToUserId { get; set; }

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
    [Key, Column(Order = 0, TypeName = "uuid")]
    public Guid UserId { get; set; }

    [Key, Column(Order = 1, TypeName = "uuid")]
    public Guid SuggestedUserId { get; set; }

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
// ===== CHATS MODELS                                =====
// =======================================================
public class Message
{
    [Key]
    [Column(TypeName = "uuid")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column(TypeName = "uuid")]
    public Guid FromUserId { get; set; }

    [Required]
    [Column(TypeName = "uuid")]
    public Guid ToUserId { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReadAt { get; set; }

    public bool IsRead { get; set; } = false;

    [JsonIgnore]
    [ForeignKey(nameof(FromUserId))]
    public User FromUser { get; set; } = null!;

    [JsonIgnore]
    [ForeignKey(nameof(ToUserId))]
    public User ToUser { get; set; } = null!;
}

// =======================================================
// ====               DTO MAPPERS                    =====
// =======================================================
public static class DtoMappers
{
    public class SpotifySongDto
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string? PreviewUrl { get; set; }
        public string? SpotifyUri { get; set; }
        public string? SpotifyUrl { get; set; }
        public string? DeezerPreviewUrl { get; set; }
    }

    public record BasicProfileUpdateRequest(
        int Age,
        string Gender,
        string SexualOrientation,
        string? Bio = null
    );

    public static UserDto ToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Age = user.Age,
            Email = user.Email,
            Location = user.Location ?? "",
            Bio = user.Bio ?? "",
            Gender = user.Gender,
            SexualOrientation = user.SexualOrientation,
            Images = user.Images.Select(i => i.ImageUrl).ToList(),
            MusicProfile = user.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = user.MusicProfile.FavoriteArtists ?? new List<string>(),
                FavoriteGenres = user.MusicProfile.FavoriteGenres ?? new List<string>(),
                FavoriteSongs = user.MusicProfile.FavoriteSongs ?? new List<string>(),
            } : new MusicProfileDto()
        };
    }
}

// =======================================================
// ===== DTOs (Data Transfer Objects) and Requests =======
// =======================================================
public class MusicProfileDto
{
    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public List<string> FavoriteSongs { get; set; } = new();
}

public class UserDto
{
    public Guid Id { get; set; }
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

    public string? Bio { get; set; }

    public string? ProfileImage { get; set; }
}

public class LoginRequestFromApp
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class CompatibilityResult : UserDto
{
    public double CompatibilityScore { get; set; }
}

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

public record UpdateProfileDto(string? Genres, string? Artists, string? Songs);

public record SwipeDto(Guid FromUserId, Guid ToUserId, bool IsLike);

public record SendLikeDto(Guid FromUserId, Guid ToUserId);

public record LikeDto(Guid FromUserId, Guid ToUserId, bool IsLike);

public record BatchCalculateRequest(
    Guid UserId,
    List<Guid> TargetUserIds
);

public class ResponseMessage
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public record UpdateMusicProfileRequest(
    string Artists,
    string Songs,
    string Genres
);

public static class Endpoints
{
    public static async Task<IResult> CreateUser(AppDbContext db, CreateUserDto dto)
    {
        var user = new User
        {
            Name = dto.Name,
            Age = dto.Age,
            Gender = dto.Gender,
            Email = dto.Email,
            PasswordHash = PasswordHasher.HashPassword(dto.Password),
            MusicProfile = new MusicProfile
            {
                FavoriteGenres = dto.Genres.Split(',').Select(g => g.Trim()).ToList(),
                FavoriteArtists = dto.Artists.Split(',').Select(a => a.Trim()).ToList(),
                FavoriteSongs = dto.Songs.Split(',').Select(s => s.Trim()).ToList()
            }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Results.Ok(new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Age = user.Age,
            Email = user.Email,
            MusicProfile = user.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = user.MusicProfile.FavoriteArtists,
                FavoriteGenres = user.MusicProfile.FavoriteGenres,
                FavoriteSongs = user.MusicProfile.FavoriteSongs,
            } : new MusicProfileDto()
        });
    }

    public static async Task<IResult> GetUser(AppDbContext db, Guid id)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");
        return Results.Ok(user);
    }

    public static async Task<IResult> UpdateProfile(AppDbContext db, Guid id, UpdateProfileDto dto)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");

        if (user.MusicProfile == null)
        {
            user.MusicProfile = new MusicProfile
            {
                UserId = id,
                FavoriteGenres = new List<string>(),
                FavoriteArtists = new List<string>(),
                FavoriteSongs = new List<string>()
            };
        }

        if (dto.Genres != null)
            user.MusicProfile.FavoriteGenres = dto.Genres.Split(',').Select(g => g.Trim()).ToList();
        if (dto.Artists != null)
            user.MusicProfile.FavoriteArtists = dto.Artists.Split(',').Select(a => a.Trim()).ToList();
        if (dto.Songs != null)
            user.MusicProfile.FavoriteSongs = dto.Songs.Split(',').Select(s => s.Trim()).ToList();

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

    public static async Task<IResult> AddUserImage(AppDbContext db, Guid id, UserImage image)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return Results.NotFound("User not found");

        image.UserId = id;
        if (string.IsNullOrEmpty(image.Url) && !string.IsNullOrEmpty(image.ImageUrl))
        {
            image.Url = image.ImageUrl;
        }

        db.UserImages.Add(image);
        await db.SaveChangesAsync();

        return Results.Ok(image);
    }

    public static async Task<IResult> GetUserImages(AppDbContext db, Guid id)
    {
        var user = await db.Users
            .Include(u => u.Images)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound("User not found");

        return Results.Ok(user.Images.Select(i => i.Url).ToList());
    }
}