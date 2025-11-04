using System.Text;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Spotilove;
// =======================================================
// ===== DATABASE CONTEXT =====
// =======================================================
public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<MusicProfile> MusicProfiles { get; set; } = null!;
    public DbSet<Like> Likes { get; set; } = null!;
    public DbSet<UserImage> UserImages { get; set; } = null!;
    public DbSet<UserSuggestionQueue> UserSuggestionQueues { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        // ===== Like Relationships (Composite Key) =====
        // 1. Define the Composite Primary Key: (FromUserId, ToUserId)
        modelBuilder.Entity<Like>()
            .HasKey(l => new { l.FromUserId, l.ToUserId });

        // 2. FromUser (The user initiating the swipe)
        modelBuilder.Entity<Like>()
            .HasOne(l => l.FromUser)
            .WithMany(u => u.Likes) // Assuming 'Likes' is the property on User for outgoing swipes (as per your previous model fix)
            .HasForeignKey(l => l.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // 3. ToUser (The user being swiped on)
        modelBuilder.Entity<Like>()
            .HasOne(l => l.ToUser)
            .WithMany(u => u.LikesReceived) // Assuming 'LikesReceived' or similar is defined on User for incoming swipes
            .HasForeignKey(l => l.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // OPTIMIZATION: Composite unique index (already handled by composite primary key, but good to ensure)
        modelBuilder.Entity<Like>()
            .HasIndex(l => new { l.FromUserId, l.ToUserId })
            .IsUnique();

        // ===== UserSuggestionQueue Configuration (Composite Key) =====
        modelBuilder.Entity<UserSuggestionQueue>(entity =>
        {
            // FIX: Must define a COMPOSITE key for uniqueness per user
            entity.HasKey(e => new { e.UserId, e.SuggestedUserId });

            // The user whose queue this is
            entity.HasOne(e => e.User)
                .WithMany(u => u.Suggestions) // Assuming 'Suggestions' collection on User
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The user being suggested
            entity.HasOne(e => e.SuggestedUser)
                .WithMany() // No navigation property needed on SuggestedUser side
                .HasForeignKey(e => e.SuggestedUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // OPTIMIZATION: Index for queue position lookups
            entity.HasIndex(e => new { e.UserId, e.QueuePosition });

            // OPTIMIZATION: Index for score-based queries
            entity.HasIndex(e => new { e.UserId, e.CompatibilityScore });
        });

        // OPTIMIZATION: Index on Email for auth lookups
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique(); // Email must be unique for registration
    }
}


// ===== PASSWORD UTILS =====
public static class PasswordHasher
{
    public static async Task<IResult> SendLike(AppDbContext db, SendLikeDto dto)
    {
        // 1. Basic validation
        if (dto.FromUserId == dto.ToUserId)
        {
            return Results.BadRequest(new ResponseMessage { Success = false, Message = "Cannot like yourself." });
        }

        // Check if a like already exists from FromUser to ToUser (prevent duplicate swipes)
        if (await db.Likes.AnyAsync(l => l.FromUserId == dto.FromUserId && l.ToUserId == dto.ToUserId))
        {
            return Results.Conflict(new ResponseMessage { Success = false, Message = $"User {dto.FromUserId} already swiped on user {dto.ToUserId}." });
        }

        // 2. Check for an existing reverse like (potential match)
        var reverseLike = await db.Likes
            .FirstOrDefaultAsync(l =>
                l.FromUserId == dto.ToUserId &&
                l.ToUserId == dto.FromUserId);

        bool isMatch = false;

        // 3. Create the new outgoing like
        var newLike = new Like
        {
            FromUserId = dto.FromUserId,
            ToUserId = dto.ToUserId,
            IsMatch = false, // Default to false
            CreatedAt = DateTime.UtcNow
        };

        if (reverseLike != null)
        {
            // MUTUAL MATCH!
            isMatch = true;

            // Update the existing reverse like to reflect the match
            reverseLike.IsMatch = true;
            db.Likes.Update(reverseLike);

            // Set the new outgoing like to reflect the match
            newLike.IsMatch = true;
        }

        db.Likes.Add(newLike);
        await db.SaveChangesAsync();

        if (isMatch)
        {
            // Return a success message indicating a match
            return Results.Ok(new ResponseMessage
            {
                Success = true,
                Message = $"MATCH! You and user {dto.ToUserId} liked each other."
            });
        }
        else
        {
            // Return a success message indicating a successful swipe
            return Results.Created($"/likes/{dto.FromUserId}/{dto.ToUserId}", new ResponseMessage
            {
                Success = true,
                Message = $"Successfully liked user {dto.ToUserId}. Waiting for them to like back."
            });
        }
    }
    private static UserDto ToUserDto(User user) => new()
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

    private const string Salt = "SpotiLove_Salt";

    public static string HashPassword(string password)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password + Salt);
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

    /// <summary>
    /// Creates a new user with initial auth details and music profile.
    /// </summary>
    public static async Task<IResult> CreateUser(AppDbContext db, CreateUserDto dto)
    {
        // 1. Check for existing user
        if (await db.Users.AnyAsync(u => u.Email == dto.Email))
        {
            return Results.Conflict(new ResponseMessage { Success = false, Message = "A user with this email already exists." });
        }

        var user = new User
        {
            Name = dto.Name,
            Age = dto.Age,
            Gender = dto.Gender,
            Email = dto.Email,
            PasswordHash = PasswordHasher.HashPassword(dto.Password), // Hashing the password
            MusicProfile = new MusicProfile
            {
                FavoriteGenres = dto.Genres,
                FavoriteArtists = dto.Artists,
                FavoriteSongs = dto.Songs
            }
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Return the clean DTO
        return Results.Created($"/users/{user.Id}", ToUserDto(user));
    }

    /// <summary>
    /// Gets a user profile by ID, returning a clean DTO.
    /// </summary>
    public static async Task<IResult> GetUser(AppDbContext db, int id)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound(new ResponseMessage { Success = false, Message = "User not found" });

        // FIX: Return a clean DTO instead of the full entity
        return Results.Ok(ToUserDto(user));
    }

    /// <summary>
    /// Updates only the music profile fields.
    /// </summary>
    public static async Task<IResult> UpdateProfile(AppDbContext db, int id, UpdateProfileDto dto)
    {
        var user = await db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return Results.NotFound(new ResponseMessage { Success = false, Message = "User not found" });
        if (user.MusicProfile == null)
        {
            // Create a profile if it doesn't exist
            user.MusicProfile = new MusicProfile { UserId = id };
        }

        // Apply updates only if the DTO field is not null
        if (dto.Genres != null) user.MusicProfile.FavoriteGenres = dto.Genres;
        if (dto.Artists != null) user.MusicProfile.FavoriteArtists = dto.Artists;
        if (dto.Songs != null) user.MusicProfile.FavoriteSongs = dto.Songs;

        await db.SaveChangesAsync();

        // FIX: Return a clean DTO instead of the full entity
        // Re-fetch to ensure all properties (like image URLs) are included if needed, 
        // but for simplicity, we rely on the tracked entity for now.
        var updatedUser = await db.Users.Include(u => u.MusicProfile).Include(u => u.Images).FirstAsync(u => u.Id == id);
        return Results.Ok(ToUserDto(updatedUser));
    }

    /// <summary>
    /// Returns a list of all users as DTOs.
    /// </summary>
    public static async Task<IResult> SearchUsers(AppDbContext db)
    {
        var users = await db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .AsNoTracking()
            .ToListAsync();

        var userDtos = users.Select(ToUserDto).ToList();

        return Results.Ok(new TakeExUsersResponse
        {
            Success = true,
            Count = userDtos.Count,
            Users = userDtos
        });
    }

    /// <summary>
    /// Adds a new image URL to a user's profile.
    /// </summary>
    public static async Task<IResult> AddUserImage(AppDbContext db, int id, UserImage image)
    {
        var userExists = await db.Users.AnyAsync(u => u.Id == id);
        if (!userExists) return Results.NotFound(new ResponseMessage { Success = false, Message = "User not found" });

        // Ensure the image URL is set correctly (simplified)
        image.UserId = id;

        db.UserImages.Add(image);
        await db.SaveChangesAsync();

        return Results.Created($"/users/{id}/images", new { image.Id, image.Url });
    }

    /// <summary>
    /// Gets all images for a specific user.
    /// </summary>
    public static async Task<IResult> GetUserImages(AppDbContext db, int id)
    {
        var images = await db.UserImages
            .Where(i => i.UserId == id)
            .Select(i => i.Url)
            .ToListAsync();

        if (images.Count == 0) return Results.NotFound(new ResponseMessage { Success = false, Message = "No images found for user" });

        return Results.Ok(images);
    }
}
