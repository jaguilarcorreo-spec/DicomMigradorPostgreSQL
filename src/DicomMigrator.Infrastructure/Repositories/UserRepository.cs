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
        user.SecurityStamp = NewStamp();
        await using var db = factory.CreateDbContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateAsync(AppUser user)
    {
        await using var db = factory.CreateDbContext();

        // Sello de seguridad (SEC-2): se renueva si cambia algo que afecta a lo que la
        // sesión puede hacer (rol, activo, contraseña), lo que invalida las sesiones
        // abiertas de ese usuario. Si no, se conserva el de la BD: el objeto recibido
        // puede llevar un sello antiguo (p. ej. cargado en la pantalla de usuarios antes
        // de que el usuario cerrara sesión) y no debe pisarlo.
        var current = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == user.Id)
            .Select(u => new { u.Role, u.IsActive, u.PasswordHash, u.SecurityStamp })
            .FirstOrDefaultAsync();
        var sensitiveChange = current is null
            || current.Role != user.Role
            || current.IsActive != user.IsActive
            || current.PasswordHash != user.PasswordHash;
        user.SecurityStamp = sensitiveChange || string.IsNullOrEmpty(current?.SecurityStamp)
            ? NewStamp()
            : current!.SecurityStamp;

        db.AppUsers.Update(user);
        await db.SaveChangesAsync();
    }

    public async Task RotateSecurityStampAsync(int userId)
    {
        await using var db = factory.CreateDbContext();
        await db.AppUsers.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, NewStamp()));
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    public async Task<int> CountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AppUsers.CountAsync();
    }
}
