using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<StreamDefinition> Streams => Set<StreamDefinition>();
    public DbSet<MediaServer> Servers => Set<MediaServer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<StreamDefinition>(e =>
        {
            // A path name only has to be unique on its own server.
            e.HasIndex(s => new { s.ServerId, s.Name }).IsUnique();
            e.HasOne(s => s.Server)
                .WithMany(m => m.Streams)
                .HasForeignKey(s => s.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Owner)
                .WithMany(u => u.Streams)
                .HasForeignKey(s => s.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<MediaServer>(e =>
        {
            e.HasIndex(m => m.Name).IsUnique();
            e.HasIndex(m => m.HookSecret).IsUnique();
        });
    }
}
