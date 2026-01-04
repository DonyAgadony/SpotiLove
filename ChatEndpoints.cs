using Microsoft.EntityFrameworkCore;

namespace Spotilove;

public static class ChatEndpoints
{
    // Get all conversations for a user
    public static async Task<IResult> GetUserConversations(AppDbContext db, Guid userId)
    {
        try
        {
            // Get all users the current user has exchanged messages with
            var conversationUserIds = await db.Messages
                .Where(m => m.FromUserId == userId || m.ToUserId == userId)
                .Select(m => m.FromUserId == userId ? m.ToUserId : m.FromUserId)
                .Distinct()
                .ToListAsync();

            if (!conversationUserIds.Any())
            {
                return Results.Ok(new
                {
                    success = true,
                    conversations = new List<object>(),
                    message = "No conversations yet"
                });
            }

            // Get user details and last message for each conversation
            var conversations = new List<object>();

            foreach (var otherUserId in conversationUserIds)
            {
                var otherUser = await db.Users
                    .Include(u => u.Images)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == otherUserId);

                if (otherUser == null) continue;

                // Get last message between these users
                var lastMessage = await db.Messages
                    .Where(m =>
                        (m.FromUserId == userId && m.ToUserId == otherUserId) ||
                        (m.FromUserId == otherUserId && m.ToUserId == userId))
                    .OrderByDescending(m => m.SentAt)
                    .FirstOrDefaultAsync();

                // Count unread messages from other user
                var unreadCount = await db.Messages
                    .CountAsync(m =>
                        m.FromUserId == otherUserId &&
                        m.ToUserId == userId &&
                        !m.IsRead);

                conversations.Add(new
                {
                    userId = otherUser.Id,
                    name = otherUser.Name,
                    profileImage = otherUser.Images.FirstOrDefault()?.ImageUrl ?? "default_user.png",
                    lastMessage = lastMessage?.Content ?? "",
                    lastMessageTime = lastMessage?.SentAt ?? DateTime.UtcNow,
                    unreadCount = unreadCount,
                    isOnline = false // You can implement online status later
                });
            }

            // Sort by last message time
            conversations = conversations
                .OrderByDescending(c => ((dynamic)c).lastMessageTime)
                .ToList();

            return Results.Ok(new
            {
                success = true,
                conversations = conversations
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error getting conversations: {ex.Message}");
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to get conversations"
            );
        }
    }

    // Get messages between two users
    public static async Task<IResult> GetMessages(
        AppDbContext db,
        Guid userId,
        Guid otherUserId,
        int limit = 50,
        int offset = 0)
    {
        try
        {
            var messages = await db.Messages
                .Where(m =>
                    (m.FromUserId == userId && m.ToUserId == otherUserId) ||
                    (m.FromUserId == otherUserId && m.ToUserId == userId))
                .OrderByDescending(m => m.SentAt)
                .Skip(offset)
                .Take(limit)
                .Select(m => new
                {
                    id = m.Id,
                    fromUserId = m.FromUserId,
                    toUserId = m.ToUserId,
                    content = m.Content,
                    sentAt = m.SentAt,
                    isRead = m.IsRead,
                    readAt = m.ReadAt
                })
                .ToListAsync();

            // Mark messages as read
            var unreadMessages = await db.Messages
                .Where(m =>
                    m.FromUserId == otherUserId &&
                    m.ToUserId == userId &&
                    !m.IsRead)
                .ToListAsync();

            foreach (var msg in unreadMessages)
            {
                msg.IsRead = true;
                msg.ReadAt = DateTime.UtcNow;
            }

            if (unreadMessages.Any())
            {
                await db.SaveChangesAsync();
            }

            return Results.Ok(new
            {
                success = true,
                messages = messages.OrderBy(m => m.sentAt).ToList(),
                count = messages.Count
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error getting messages: {ex.Message}");
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to get messages"
            );
        }
    }

    // Send a message
    public static async Task<IResult> SendMessage(
        AppDbContext db,
        SendMessageRequest request)
    {
        try
        {
            if (request.FromUserId == request.ToUserId)
            {
                return Results.BadRequest(new
                {
                    success = false,
                    message = "Cannot send message to yourself"
                });
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.BadRequest(new
                {
                    success = false,
                    message = "Message content cannot be empty"
                });
            }

            // Verify both users exist
            var fromUserExists = await db.Users.AnyAsync(u => u.Id == request.FromUserId);
            var toUserExists = await db.Users.AnyAsync(u => u.Id == request.ToUserId);

            if (!fromUserExists || !toUserExists)
            {
                return Results.NotFound(new
                {
                    success = false,
                    message = "One or both users not found"
                });
            }

            var message = new Message
            {
                FromUserId = request.FromUserId,
                ToUserId = request.ToUserId,
                Content = request.Content.Trim(),
                SentAt = DateTime.UtcNow,
                IsRead = false
            };

            db.Messages.Add(message);
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                success = true,
                message = new
                {
                    id = message.Id,
                    fromUserId = message.FromUserId,
                    toUserId = message.ToUserId,
                    content = message.Content,
                    sentAt = message.SentAt,
                    isRead = message.IsRead
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error sending message: {ex.Message}");
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to send message"
            );
        }
    }

    // Mark messages as read
    public static async Task<IResult> MarkMessagesAsRead(
        AppDbContext db,
        Guid userId,
        Guid otherUserId)
    {
        try
        {
            var unreadMessages = await db.Messages
                .Where(m =>
                    m.FromUserId == otherUserId &&
                    m.ToUserId == userId &&
                    !m.IsRead)
                .ToListAsync();

            foreach (var message in unreadMessages)
            {
                message.IsRead = true;
                message.ReadAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                success = true,
                markedCount = unreadMessages.Count
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error marking messages as read: {ex.Message}");
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to mark messages as read"
            );
        }
    }

    // Delete a message
    public static async Task<IResult> DeleteMessage(
        AppDbContext db,
        Guid messageId,
        Guid userId)
    {
        try
        {
            var message = await db.Messages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.FromUserId == userId);

            if (message == null)
            {
                return Results.NotFound(new
                {
                    success = false,
                    message = "Message not found or you don't have permission to delete it"
                });
            }

            db.Messages.Remove(message);
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                success = true,
                message = "Message deleted successfully"
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error deleting message: {ex.Message}");
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to delete message"
            );
        }
    }
}

// Request DTOs
public record SendMessageRequest(
    Guid FromUserId,
    Guid ToUserId,
    string Content
);