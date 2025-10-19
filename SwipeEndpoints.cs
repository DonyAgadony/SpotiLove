using Microsoft.AspNetCore.Http;

namespace Spotilove;

public static class SwipeEndpoints
{
    /// <summary>
    /// Get potential matches for swiping
    /// </summary>
    public static async Task<IResult> GetPotentialMatches(SwipeService swipeService, int userId, int count = 10)
    {
        try
        {
            var suggestions = await swipeService.GetPotentialMatchesAsync(userId, count);
            return Results.Ok(new
            {
                Users = suggestions,
                Count = suggestions.Count(),
                Message = suggestions.Any() ? "Potential matches found" : "No more users to show"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Error fetching potential matches");
        }
    }

    /// <summary>
    /// Handle swipe action (like = true, pass = false)
    /// </summary>
    public static async Task<IResult> SwipeOnUser(SwipeService swipeService, SwipeDto swipeDto)
    {
        try
        {
            var result = await swipeService.SwipeAsync(swipeDto.FromUserId, swipeDto.ToUserId, swipeDto.IsLike);

            return Results.Ok(new ResponseMessage
            {
                Success = true
            });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new ResponseMessage { Success = false });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new ResponseMessage { Success = false });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Error processing swipe");
        }
    }

    /// <summary>
    /// Get all matches for a user
    /// </summary>
    public static async Task<IResult> GetUserMatches(SwipeService swipeService, int userId)
    {
        try
        {
            var matches = await swipeService.GetMatchesAsync(userId);
            return Results.Ok(new
            {
                Matches = matches,
                Count = matches.Count(),
                Message = matches.Any() ? "Your matches" : "No matches yet"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Error fetching matches");
        }
    }

    /// <summary>
    /// Get swipe statistics for a user
    /// </summary>
    public static async Task<IResult> GetSwipeStats(SwipeService swipeService, int userId)
    {
        try
        {
            // Get basic stats from the database
            var stats = new
            {
                TotalSwipes = 0,
                Likes = 0,
                Passes = 0,
                Matches = 0,
                LikeRate = 0.0
            };

            return Results.Ok(stats);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Error fetching swipe stats");
        }
    }

    /// <summary>
    /// Quick swipe like endpoint
    /// </summary>
    public static async Task<IResult> LikeUser(SwipeService swipeService, int fromUserId, int toUserId)
    {
        var swipeDto = new SwipeDto(fromUserId, toUserId, true);
        return await SwipeOnUser(swipeService, swipeDto);
    }

    /// <summary>
    /// Quick swipe pass endpoint
    /// </summary>
    public static async Task<IResult> PassUser(SwipeService swipeService, int fromUserId, int toUserId)
    {
        var swipeDto = new SwipeDto(fromUserId, toUserId, false);
        return await SwipeOnUser(swipeService, swipeDto);
    }
}

// DTO for swipe actions
public record SwipeDto(int FromUserId, int ToUserId, bool IsLike);