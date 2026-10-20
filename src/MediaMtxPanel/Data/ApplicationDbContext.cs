using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<StreamDefinition> Streams => Set<StreamDefinition>();
    public DbSet<MediaServer> Servers => Set<MediaServer>();
    public DbSet<StreamOffer> Offers => Set<StreamOffer>();
    public DbSet<TicketOrder> Orders => Set<TicketOrder>();
    public DbSet<PaymentAccount> PaymentAccounts => Set<PaymentAccount>();
    public DbSet<StreamStatSample> StatSamples => Set<StreamStatSample>();

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
            e.HasIndex(s => s.WatchSlug).IsUnique();
            e.HasOne(s => s.Owner)
                .WithMany(u => u.Streams)
                .HasForeignKey(s => s.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StreamOffer>(e =>
        {
            e.HasOne(o => o.Stream).WithMany(s => s.Offers).HasForeignKey(o => o.StreamId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TicketOrder>(e =>
        {
            e.HasIndex(o => o.AccessToken);
            e.HasIndex(o => o.ProviderPaymentId);
            e.HasOne(o => o.Stream).WithMany().HasForeignKey(o => o.StreamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Offer).WithMany().HasForeignKey(o => o.OfferId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<PaymentAccount>(e =>
        {
            e.HasIndex(a => a.UserId).IsUnique();
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StreamStatSample>(e =>
        {
            e.HasIndex(x => new { x.StreamId, x.Timestamp });
            e.HasIndex(x => x.Timestamp);
            e.HasOne(x => x.Stream).WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MediaServer>(e =>
        {
            e.HasIndex(m => m.Name).IsUnique();
            e.HasIndex(m => m.HookSecret).IsUnique();
        });
    }
}
