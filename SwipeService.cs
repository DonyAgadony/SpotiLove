using Microsoft.EntityFrameworkCore;
namespace Spotilove;

public class SwipeService
{
    private readonly AppDbContext _db;
    private const int MIN_QUEUE_SIZE = 3;
    private const int BATCH_SIZE = 10;

    public SwipeService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Get potential matches for a user with queue management
    /// </summary>
    public async Task<IEnumerable<UserSuggestion>> GetPotentialMatchesAsync(int userId, int count = 10)
    {
        // Get current queue for the user
        var currentQueue = await GetUserQueueAsync(userId);

        // If queue is shorter than minimum, refill it
        if (currentQueue.Count < MIN_QUEUE_SIZE)
        {
            await RefillUserQueueAsync(userId);
            currentQueue = await GetUserQueueAsync(userId);
        }

        // Return requested number of suggestions from queue
        return currentQueue.Take(count);
    }

    /// <summary>
    /// Get the current suggestion queue for a user
    /// </summary>
    private async Task<List<UserSuggestion>> GetUserQueueAsync(int userId)
    {
        var queueItems = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .Include(q => q.SuggestedUser)
                .ThenInclude(u => u.MusicProfile)
            .Include(q => q.SuggestedUser)
                .ThenInclude(u => u.Images)
            .OrderBy(q => q.QueuePosition)
            .ToListAsync();

        var user = await _db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user?.MusicProfile == null)
            return new List<UserSuggestion>();

        return queueItems.Select(qi => CreateUserSuggestion(user, qi.SuggestedUser)).ToList();
    }

    /// <summary>
    /// Refill the user's suggestion queue
    /// </summary>
    private async Task<bool> RefillUserQueueAsync(int userId)
    {
        var user = await _db.Users
            .Include(u => u.MusicProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user?.MusicProfile == null)
            return false;

        // Get users that this user has already swiped on
        var swipedUserIds = await _db.Likes
            .Where(l => l.FromUserId == userId)
            .Select(l => l.ToUserId)
            .ToListAsync();

        // Get users already in queue
        var queuedUserIds = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .Select(q => q.SuggestedUserId)
            .ToListAsync();

        // Combine swiped and queued user IDs to exclude them
        var excludedUserIds = swipedUserIds.Concat(queuedUserIds).ToHashSet();

        // Get potential matches (excluding already swiped users, queued users, and the user themselves)
        var potentialMatches = await _db.Users
            .Include(u => u.MusicProfile)
            .Where(u => u.Id != userId &&
                       u.MusicProfile != null &&
                       !excludedUserIds.Contains(u.Id))
            .ToListAsync();

        if (!potentialMatches.Any())
            return false;

        // Calculate compatibility scores and get top matches
        var suggestions = CalculateCompatibilityScores(user, potentialMatches)
            .Take(BATCH_SIZE)
            .ToList();

        // Get current max queue position
        var maxPosition = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .MaxAsync(q => (int?)q.QueuePosition) ?? 0;

        // Add new suggestions to queue
        var queueItems = suggestions.Select((suggestion, index) => new UserSuggestionQueue
        {
            UserId = userId,
            SuggestedUserId = suggestion.Id,
            QueuePosition = maxPosition + index + 1,
            CreatedAt = DateTime.UtcNow,
            CompatibilityScore = suggestion.CompatibilityScore
        });

        _db.UserSuggestionQueues.AddRange(queueItems);
        await _db.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// Handle swipe action (like or pass) with queue management
    /// </summary>
    public async Task<SwipeResult> SwipeAsync(int fromUserId, int toUserId, bool isLike)
    {
        // Check if users exist
        var fromUser = await _db.Users.FindAsync(fromUserId);
        var toUser = await _db.Users.FindAsync(toUserId);

        if (fromUser == null || toUser == null)
            throw new ArgumentException("One or both users do not exist");

        // Check if already swiped on this user
        var existingSwipe = await _db.Likes
            .FirstOrDefaultAsync(l => l.FromUserId == fromUserId && l.ToUserId == toUserId);

        if (existingSwipe != null)
            throw new InvalidOperationException("You have already swiped on this user");

        // Create the swipe record
        var swipe = new Like
        {
            FromUserId = fromUserId,
            ToUserId = toUserId,
            IsLike = isLike
        };

        _db.Likes.Add(swipe);

        // Remove the user from the suggestion queue
        var queueItem = await _db.UserSuggestionQueues
            .FirstOrDefaultAsync(q => q.UserId == fromUserId && q.SuggestedUserId == toUserId);

        if (queueItem != null)
        {
            _db.UserSuggestionQueues.Remove(queueItem);
        }

        await _db.SaveChangesAsync();

        // Check current queue size and refill if needed
        var currentQueueSize = await _db.UserSuggestionQueues
            .CountAsync(q => q.UserId == fromUserId);

        if (currentQueueSize < MIN_QUEUE_SIZE)
        {
            _ = Task.Run(() => RefillUserQueueAsync(fromUserId)); // Fire and forget
        }

        // Check if it's a match (mutual like)
        bool isMatch = false;
        if (isLike)
        {
            isMatch = await _db.Likes.AnyAsync(l =>
                l.FromUserId == toUserId &&
                l.ToUserId == fromUserId &&
                l.IsLike);
        }

        return new SwipeResult
        {
            SwipeId = swipe.Id,
            IsMatch = isMatch,
            TargetUser = new UserSummary
            {
                Id = toUser.Id,
                Name = toUser.Name,
                Age = toUser.Age
            }
        };
    }

    /// <summary>
    /// Get all matches for a user
    /// </summary>
    public async Task<IEnumerable<MatchInfo>> GetMatchesAsync(int userId)
    {
        // Find mutual likes
        var userLikes = await _db.Likes
            .Where(l => l.FromUserId == userId && l.IsLike)
            .Select(l => l.ToUserId)
            .ToListAsync();

        var mutualMatches = await _db.Likes
            .Where(l => l.FromUserId != userId &&
                       l.ToUserId == userId &&
                       l.IsLike &&
                       userLikes.Contains(l.FromUserId))
            .Include(l => l.FromUser!)
                .ThenInclude(u => u.Images)
            .ToListAsync();

        return mutualMatches.Select(l => new MatchInfo
        {
            UserId = l.FromUserId,
            Name = l.FromUser!.Name,
            Age = l.FromUser.Age,
            ProfileImageUrl = l.FromUser.Images.FirstOrDefault()?.Url ?? "",
            MatchedAt = DateTime.UtcNow // You might want to add a timestamp field to the Like entity
        });
    }

    /// <summary>
    /// Initialize or refill queue for a user (useful for onboarding or manual queue management)
    /// </summary>
    public async Task<bool> InitializeUserQueueAsync(int userId)
    {
        // Clear existing queue
        var existingQueue = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .ToListAsync();

        _db.UserSuggestionQueues.RemoveRange(existingQueue);

        // Refill queue
        return await RefillUserQueueAsync(userId);
    }

    /// <summary>
    /// Get queue status for a user
    /// </summary>
    public async Task<QueueStatus> GetQueueStatusAsync(int userId)
    {
        var queueCount = await _db.UserSuggestionQueues
            .CountAsync(q => q.UserId == userId);

        var totalAvailableMatches = await GetTotalAvailableMatchesAsync(userId);

        return new QueueStatus
        {
            CurrentQueueSize = queueCount,
            IsQueueLow = queueCount < MIN_QUEUE_SIZE,
            TotalAvailableMatches = totalAvailableMatches,
            CanRefillQueue = totalAvailableMatches > queueCount
        };
    }

    /// <summary>
    /// Get total number of available matches for a user
    /// </summary>
    private async Task<int> GetTotalAvailableMatchesAsync(int userId)
    {
        var swipedUserIds = await _db.Likes
            .Where(l => l.FromUserId == userId)
            .Select(l => l.ToUserId)
            .ToListAsync();

        var queuedUserIds = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .Select(q => q.SuggestedUserId)
            .ToListAsync();

        var excludedUserIds = swipedUserIds.Concat(queuedUserIds).ToHashSet();

        return await _db.Users
            .CountAsync(u => u.Id != userId &&
                            u.MusicProfile != null &&
                            !excludedUserIds.Contains(u.Id));
    }

    /// <summary>
    /// Create UserSuggestion object from User entity
    /// </summary>
    private UserSuggestion CreateUserSuggestion(User currentUser, User suggestedUser)
    {
        var myGenres = currentUser.MusicProfile!.FavoriteGenres
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var myArtists = currentUser.MusicProfile.FavoriteArtists
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var theirGenres = suggestedUser.MusicProfile!.FavoriteGenres
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var theirArtists = suggestedUser.MusicProfile.FavoriteArtists
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        double genreCompatibility = CalculateJaccardSimilarity(myGenres, theirGenres);
        double artistCompatibility = CalculateJaccardSimilarity(myArtists, theirArtists);
        double overallScore = (genreCompatibility * 0.6) + (artistCompatibility * 0.4);

        return new UserSuggestion
        {
            Id = suggestedUser.Id,
            Name = suggestedUser.Name,
            Age = suggestedUser.Age,
            Gender = suggestedUser.Gender,
            ProfileImages = suggestedUser.Images.Select(img => img.Url).ToList(),
            FavoriteGenres = theirGenres.ToList(),
            FavoriteArtists = theirArtists.ToList(),
            CompatibilityScore = Math.Round(overallScore * 100, 1),
            GenreCompatibility = Math.Round(genreCompatibility * 100, 1),
            ArtistCompatibility = Math.Round(artistCompatibility * 100, 1)
        };
    }

    /// <summary>
    /// Calculate compatibility scores based on music preferences
    /// </summary>
    private IEnumerable<UserSuggestion> CalculateCompatibilityScores(User user, List<User> potentialMatches)
    {
        var myGenres = user.MusicProfile!.FavoriteGenres
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var myArtists = user.MusicProfile.FavoriteArtists
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return potentialMatches.Select(match =>
        {
            var theirGenres = match.MusicProfile!.FavoriteGenres
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var theirArtists = match.MusicProfile.FavoriteArtists
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            double genreCompatibility = CalculateJaccardSimilarity(myGenres, theirGenres);
            double artistCompatibility = CalculateJaccardSimilarity(myArtists, theirArtists);

            // Weight genres slightly higher than artists
            double overallScore = (genreCompatibility * 0.6) + (artistCompatibility * 0.4);

            return new UserSuggestion
            {
                Id = match.Id,
                Name = match.Name,
                Age = match.Age,
                Gender = match.Gender,
                ProfileImages = match.Images.Select(img => img.Url).ToList(),
                FavoriteGenres = match.MusicProfile.FavoriteGenres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                FavoriteArtists = match.MusicProfile.FavoriteArtists.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                CompatibilityScore = Math.Round(overallScore * 100, 1), // Convert to percentage
                GenreCompatibility = Math.Round(genreCompatibility * 100, 1),
                ArtistCompatibility = Math.Round(artistCompatibility * 100, 1)
            };
        })
        .OrderByDescending(x => x.CompatibilityScore);
    }

    /// <summary>
    /// Calculate Jaccard similarity coefficient between two string arrays
    /// </summary>
    private static double CalculateJaccardSimilarity(string[] set1, string[] set2)
    {
        if (set1.Length == 0 && set2.Length == 0) return 1.0;
        if (set1.Length == 0 || set2.Length == 0) return 0.0;

        var hashSet1 = new HashSet<string>(set1, StringComparer.OrdinalIgnoreCase);
        var hashSet2 = new HashSet<string>(set2, StringComparer.OrdinalIgnoreCase);

        var intersection = hashSet1.Intersect(hashSet2).Count();
        var union = hashSet1.Union(hashSet2).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Get swipe statistics for a user
    /// </summary>
    public async Task<SwipeStats> GetSwipeStatsAsync(int userId)
    {
        var swipes = await _db.Likes
            .Where(l => l.FromUserId == userId)
            .ToListAsync();

        var totalSwipes = swipes.Count;
        var likes = swipes.Count(s => s.IsLike);
        var passes = swipes.Count(s => !s.IsLike);

        var matches = await GetMatchesAsync(userId);
        var matchCount = matches.Count();

        return new SwipeStats
        {
            TotalSwipes = totalSwipes,
            Likes = likes,
            Passes = passes,
            Matches = matchCount,
            LikeRate = totalSwipes > 0 ? Math.Round((double)likes / totalSwipes * 100, 1) : 0
        };
    }
}

// DTOs and Models
public class UserSuggestion
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public List<string> ProfileImages { get; set; } = new();
    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public double CompatibilityScore { get; set; }
    public double GenreCompatibility { get; set; }
    public double ArtistCompatibility { get; set; }
}

public class SwipeResult
{
    public int SwipeId { get; set; }
    public bool IsMatch { get; set; }
    public UserSummary TargetUser { get; set; } = new();
}

public class UserSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class MatchInfo
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string ProfileImageUrl { get; set; } = string.Empty;
    public DateTime MatchedAt { get; set; }
}

public class SwipeStats
{
    public int TotalSwipes { get; set; }
    public int Likes { get; set; }
    public int Passes { get; set; }
    public int Matches { get; set; }
    public double LikeRate { get; set; }
}

public class QueueStatus
{
    public int CurrentQueueSize { get; set; }
    public bool IsQueueLow { get; set; }
    public int TotalAvailableMatches { get; set; }
    public bool CanRefillQueue { get; set; }
}

public record SwipeDto(int FromUserId, int ToUserId, bool IsLike);