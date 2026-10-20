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
    public DbSet<RestreamTarget> RestreamTargets => Set<RestreamTarget>();
    public DbSet<ViewerDailyStat> ViewerDailyStats => Set<ViewerDailyStat>();
    public DbSet<ViewerVisit> ViewerVisits => Set<ViewerVisit>();
    public DbSet<StreamEmbed> Embeds => Set<StreamEmbed>();
    public DbSet<ViewerLoginCode> ViewerLoginCodes => Set<ViewerLoginCode>();

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

        builder.Entity<RestreamTarget>(e =>
        {
            e.HasOne(t => t.Stream).WithMany(s => s.RestreamTargets).HasForeignKey(t => t.StreamId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TicketOrder>(e =>
        {
            e.HasIndex(o => o.AccessToken);
            e.HasIndex(o => o.ProviderPaymentId);
            e.HasIndex(o => o.Code).IsUnique();
            e.HasIndex(o => o.BuyerEmail);
            e.HasOne(o => o.Stream).WithMany().HasForeignKey(o => o.StreamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Offer).WithMany().HasForeignKey(o => o.OfferId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StreamEmbed>(e =>
        {
            e.HasOne(x => x.Stream).WithOne().HasForeignKey<StreamEmbed>(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.Look);
        });

        builder.Entity<ViewerLoginCode>(e => e.HasIndex(x => new { x.Email, x.CreatedAt }));

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

        builder.Entity<ViewerDailyStat>(e =>
        {
            e.HasIndex(x => new { x.StreamId, x.Day }).IsUnique();
            e.HasIndex(x => x.Day);
        });

        builder.Entity<ViewerVisit>(e =>
        {
            e.HasIndex(x => new { x.Day, x.StreamId, x.ViewerKey }).IsUnique();
            e.HasIndex(x => new { x.StreamId, x.Day });
        });

        builder.Entity<MediaServer>(e =>
        {
            e.HasIndex(m => m.Name).IsUnique();
            e.HasIndex(m => m.HookSecret).IsUnique();
        });
    }
}
