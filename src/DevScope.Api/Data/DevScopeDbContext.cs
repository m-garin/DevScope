using DevScope.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Data;

public sealed class DevScopeDbContext(DbContextOptions<DevScopeDbContext> options) : DbContext(options)
{
    public DbSet<CachedReport> CachedReports => Set<CachedReport>();
}

