using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Repositories;

public class UserRepository(IDbContextFactory<AppDbContext> factory) : IUserRepository
{
    public async Task<AppUser?> GetByUserNameAsync(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        var norm = userName.Trim().ToLowerInvariant();
        await using var db = factory.CreateDbContext();
        // El índice único está sobre el nombre ya normalizado, así que la
        // comparación se hace siempre en minúsculas.
        return await db.AppUsers.FirstOrDefaultAsync(u => u.UserName == norm);
    }

    public async Task<AppUser?> GetByIdAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<List<AppUser>> GetAllAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AppUsers.OrderBy(u => u.UserName).ToListAsync();
    }

    public async Task<AppUser> AddAsync(AppUser user)
    {
        user.UserName = user.UserName.Trim().ToLowerInvariant();
        await using var db = factory.CreateDbContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateAsync(AppUser user)
    {
        await using var db = factory.CreateDbContext();
        db.AppUsers.Update(user);
        await db.SaveChangesAsync();
    }

    public async Task<int> CountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AppUsers.CountAsync();
    }
}
