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

            var responseMessage = swipeDto.IsLike
                ? (result.IsMatch ? "It's a match! 🎉" : "You liked this user")
                : "You passed on this user";

            return Results.Ok(new
            {
                SwipeId = result.SwipeId,
                IsMatch = result.IsMatch,
                Action = swipeDto.IsLike ? "like" : "pass",
                Message = responseMessage,
                TargetUser = result.TargetUser
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { Error = ex.Message });
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
            var stats = await swipeService.GetSwipeStatsAsync(userId);
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