using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Repositories;

/// <summary>Fila única (Id=1) del estado de licencia. La crea si no existe.</summary>
public class LicenseStateRepository(IDbContextFactory<AppDbContext> factory) : ILicenseStateRepository
{
    public async Task<LicenseState> GetAsync()
    {
        await using var db = factory.CreateDbContext();
        var state = await db.LicenseStates.FirstOrDefaultAsync(x => x.Id == 1);
        if (state is null)
        {
            state = new LicenseState
            {
                Id          = 1,
                LastSeenUtc = DateTime.UtcNow,
                UpdatedUtc  = DateTime.UtcNow,
            };
            db.LicenseStates.Add(state);
            await db.SaveChangesAsync();
        }
        return state;
    }

    public async Task SaveAsync(LicenseState state)
    {
        await using var db = factory.CreateDbContext();
        state.Id         = 1;
        state.UpdatedUtc = DateTime.UtcNow;
        db.LicenseStates.Update(state);
        await db.SaveChangesAsync();
    }
}
