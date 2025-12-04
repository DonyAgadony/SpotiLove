using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public class SwipeService
{
    private readonly AppDbContext _db;

    public SwipeService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ResponseMessage> SwipeAsync(Guid fromUserId, Guid toUserId, bool isLike)
    {
        if (fromUserId == toUserId)
            throw new ArgumentException("Cannot swipe on yourself");

        // Check if swipe already exists
        var existingSwipe = await _db.Likes
            .FirstOrDefaultAsync(l => l.FromUserId == fromUserId && l.ToUserId == toUserId);

        if (existingSwipe != null)
        {
            existingSwipe.IsLike = isLike; // Update existing swipe
            existingSwipe.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            // Add new swipe
            var like = new Like
            {
                FromUserId = fromUserId,
                ToUserId = toUserId,
                IsLike = isLike,
                CreatedAt = DateTime.UtcNow
            };
            _db.Likes.Add(like);
        }

        // Optionally remove user from suggestion queue
        var queueItem = await _db.UserSuggestionQueues
            .FirstOrDefaultAsync(q => q.UserId == fromUserId && q.SuggestedUserId == toUserId);

        if (queueItem != null)
        {
            _db.UserSuggestionQueues.Remove(queueItem);
        }

        await _db.SaveChangesAsync();
        return new ResponseMessage { Success = true };
    }

    public async Task<List<UserDto>> GetPotentialMatchesAsync(Guid userId, int count = 10)
    {
        var queueItems = await _db.UserSuggestionQueues
            .Where(q => q.UserId == userId)
            .OrderByDescending(q => q.CompatibilityScore)
            .Take(count)
            .ToListAsync();

        var userIds = queueItems.Select(q => q.SuggestedUserId).ToList();

        var users = await _db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync();

        return users.Select(u => new UserDto
        {
            Id = u.Id,
            Name = u.Name,
            Age = u.Age,
            Location = u.Location ?? "",
            Bio = u.Bio ?? "",
            MusicProfile = u.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = u.MusicProfile.FavoriteArtists,
                FavoriteGenres = u.MusicProfile.FavoriteGenres,
                FavoriteSongs = u.MusicProfile.FavoriteSongs
            } : new MusicProfileDto(),
            Images = u.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
        }).ToList();
    }

    public async Task<List<UserDto>> GetMatchesAsync(Guid userId)
    {
        var mutualLikes = await _db.Likes
            .Where(l => l.FromUserId == userId && l.IsLike)
            .Join(_db.Likes,
                l1 => l1.ToUserId,
                l2 => l2.FromUserId,
                (l1, l2) => new { l1, l2 })
            .Where(x => x.l2.ToUserId == userId && x.l2.IsLike)
            .Select(x => x.l1.ToUserId)
            .ToListAsync();

        var users = await _db.Users
            .Include(u => u.MusicProfile)
            .Include(u => u.Images)
            .Where(u => mutualLikes.Contains(u.Id))
            .ToListAsync();

        return users.Select(u => new UserDto
        {
            Id = u.Id,
            Name = u.Name,
            Age = u.Age,
            Location = u.Location ?? "",
            Bio = u.Bio ?? "",
            MusicProfile = u.MusicProfile != null ? new MusicProfileDto
            {
                FavoriteArtists = u.MusicProfile.FavoriteArtists,
                FavoriteGenres = u.MusicProfile.FavoriteGenres,
                FavoriteSongs = u.MusicProfile.FavoriteSongs
            } : new MusicProfileDto(),
            Images = u.Images.Select(i => i.ImageUrl ?? i.Url).ToList()
        }).ToList();
    }
}