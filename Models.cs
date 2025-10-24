using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System;

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
    public int Id { get; set; }

    // Core Profile Data
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public string? Location { get; set; }

    // Authentication & Auditing
    public string? Email { get; set; }
    [JsonIgnore] // Typically ignore the hash for API responses
    public string? PasswordHash { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    public MusicProfile? MusicProfile { get; set; }
    public List<UserImage> Images { get; set; } = new();

    // Tracks swipes initiated by this user (FromUser)
    public List<Like> Swipes { get; set; } = new();
}
public class MusicProfile
{
    // Primary Key (often same as UserId for a 1:1 relationship)
    public int Id { get; set; }
    public int UserId { get; set; } // Foreign Key to User

    // Music Data (Stored as strings, often JSON or comma-separated in real apps)
    public string FavoriteGenres { get; set; } = "";
    public string FavoriteArtists { get; set; } = "";
    public string FavoriteSongs { get; set; } = "";

    // Navigation Property
    public User? User { get; set; }
}
public class UserImage
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    // Navigation Property
    public User? User { get; set; }
}
public class Like
{
    // Composite Key: (FromUserId, ToUserId)
    [Key, Column(Order = 0)]
    public int FromUserId { get; set; }

    [Key, Column(Order = 1)]
    public int ToUserId { get; set; }

    // True for like (swipe right), false for dislike (swipe left)
    public bool IsLike { get; set; }

    // True if the ToUser has also liked the FromUser (resulting in a match)
    public bool IsMatch { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    public User FromUser { get; set; } = null!;
    public User ToUser { get; set; } = null!;
}
public class UserSuggestionQueue
{
    // Composite Key: (UserId, SuggestedUserId)
    [Key, Column(Order = 0)]
    public int UserId { get; set; }

    [Key, Column(Order = 1)]
    public int SuggestedUserId { get; set; }

    public double CompatibilityScore { get; set; }
    public int QueuePosition { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    public User User { get; set; } = null!;
    public User SuggestedUser { get; set; } = null!;
}


// =======================================================
// ===== DATA TRANSFER OBJECTS (DTOS) ====================
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

    // Note: Email should generally be omitted from public-facing DTOs unless needed for a specific user management view.
    // I'm keeping it since you included it, but marking it nullable for consistency.
    public string? Email { get; set; }

    public MusicProfileDto? MusicProfile { get; set; }

    // List of Image URLs
    public List<string> Images { get; set; } = new List<string>();
}

