using DevScope.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Data;

public sealed class DevScopeDbContext(DbContextOptions<DevScopeDbContext> options) : DbContext(options)
{
    public DbSet<CachedReport> CachedReports => Set<CachedReport>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedReport>()
            .HasIndex(item => item.CacheKey)
            .IsUnique();

        modelBuilder.Entity<ApplicationSetting>()
            .Property(item => item.Id)
            .ValueGeneratedNever();
    }
}

