// ══════════════════════════════════════════════════════════════════════════════
//  NotificationRepository.cs  (v227)
//  Persistencia de la config de notificaciones (fila única Id=1) y del outbox.
// ══════════════════════════════════════════════════════════════════════════════
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Repositories;

public class NotificationRepository(IDbContextFactory<AppDbContext> factory) : INotificationRepository
{
    public async Task<NotificationSettings> GetSettingsAsync()
    {
        await using var db = factory.CreateDbContext();
        // Fila única Id=1. Si aún no se ha guardado, devolver valores por defecto
        // (Enabled=false) SIN persistir: así todo está apagado hasta que el admin guarde.
        return await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1)
               ?? new NotificationSettings();
    }

    public async Task SaveSettingsAsync(NotificationSettings settings)
    {
        await using var db = factory.CreateDbContext();
        settings.Id = 1;
        settings.UpdatedUtc = DateTime.UtcNow;

        var existing = await db.NotificationSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (existing is null)
            db.NotificationSettings.Add(settings);
        else
            db.Entry(existing).CurrentValues.SetValues(settings);

        await db.SaveChangesAsync();
    }

    public async Task AddToOutboxAsync(NotificationOutbox item)
    {
        await using var db = factory.CreateDbContext();
        db.NotificationOutbox.Add(item);
        await db.SaveChangesAsync();
    }

    public async Task<List<NotificationOutbox>> GetPendingAsync(int maxAttempts, int take = 50)
    {
        await using var db = factory.CreateDbContext();
        return await db.NotificationOutbox
            .Where(o => o.Status == "Pending" && o.Attempts < maxAttempts)
            .OrderBy(o => o.Id)
            .Take(take)
            .ToListAsync();
    }

    public async Task MarkSentAsync(long id)
    {
        await using var db = factory.CreateDbContext();
        await db.NotificationOutbox.Where(o => o.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, "Sent")
                .SetProperty(o => o.SentUtc, DateTime.UtcNow)
                .SetProperty(o => o.LastAttemptUtc, DateTime.UtcNow));
    }

    public async Task MarkAttemptAsync(long id, int attempts, string error, bool giveUp)
    {
        await using var db = factory.CreateDbContext();
        await db.NotificationOutbox.Where(o => o.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Attempts, attempts)
                .SetProperty(o => o.LastError, error)
                .SetProperty(o => o.LastAttemptUtc, DateTime.UtcNow)
                .SetProperty(o => o.Status, giveUp ? "Failed" : "Pending"));
    }
}
