using System;
using System.Threading.Tasks;
using PropSeekr.DTOs.Notifications;

namespace PropSeekr.Services.Interfaces;

public interface INotificationService
{
    Task<NotificationListResponseDto> GetNotificationsAsync(Guid userId, int page, int limit, string filter);
    Task<bool> MarkAsReadAsync(Guid notificationId, Guid userId);
    Task<bool> MarkAllAsReadAsync(Guid userId);
    Task<UnlockBrokerResponseDto> UnlockBrokerContactAsync(Guid notificationId, Guid userId);
}
