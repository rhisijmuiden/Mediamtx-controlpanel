using MediaMtxPanel.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public record UserSummary(
    string Id, string Email, string? DisplayName, string Role, bool Locked, int StreamCount, DateTime CreatedAt);

/// <summary>
/// User management for administrators. Each call uses its own scope so that interactive
/// components never hold on to a long-lived UserManager/DbContext.
/// </summary>
public class UserAdminService(IServiceScopeFactory scopeFactory)
{
    public async Task<List<UserSummary>> ListAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var admins = await (from ur in db.UserRoles
                            join r in db.Roles on ur.RoleId equals r.Id
                            where r.Name == Roles.Admin
                            select ur.UserId).ToListAsync();
        var users = await db.Users
            .Select(u => new { u, Count = u.Streams.Count })
            .OrderBy(x => x.u.Email)
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        return users.Select(x => new UserSummary(
            x.u.Id, x.u.Email ?? x.u.UserName ?? "", x.u.DisplayName,
            admins.Contains(x.u.Id) ? Roles.Admin : Roles.User,
            x.u.LockoutEnd is { } end && end > now,
            x.Count, x.u.CreatedAt)).ToList();
    }

    public async Task<UserSummary?> GetAsync(string id) => (await ListAsync()).FirstOrDefault(u => u.Id == id);

    public async Task<IdentityResult> CreateAsync(string email, string? displayName, string password, string role)
    {
        using var scope = scopeFactory.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };
        var result = await um.CreateAsync(user, password);
        if (!result.Succeeded)
            return result;
        return await um.AddToRoleAsync(user, Roles.All.Contains(role) ? role : Roles.User);
    }

    public async Task<IdentityResult> UpdateAsync(string id, string email, string? displayName, string role, string currentAdminId)
    {
        using var scope = scopeFactory.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(id);
        if (user is null)
            return Failed("Gebruiker niet gevonden.");

        if (id == currentAdminId && role != Roles.Admin)
            return Failed("Je kunt je eigen beheerdersrol niet intrekken.");

        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            var r = await um.SetEmailAsync(user, email);
            if (!r.Succeeded) return r;
            r = await um.SetUserNameAsync(user, email);
            if (!r.Succeeded) return r;
            user.EmailConfirmed = true;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var update = await um.UpdateAsync(user);
        if (!update.Succeeded)
            return update;

        var target = Roles.All.Contains(role) ? role : Roles.User;
        var current = await um.GetRolesAsync(user);
        if (!current.SequenceEqual([target]))
        {
            if (current.Count > 0)
                await um.RemoveFromRolesAsync(user, current);
            return await um.AddToRoleAsync(user, target);
        }
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> ResetPasswordAsync(string id, string password)
    {
        using var scope = scopeFactory.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(id);
        if (user is null)
            return Failed("Gebruiker niet gevonden.");
        var token = await um.GeneratePasswordResetTokenAsync(user);
        return await um.ResetPasswordAsync(user, token, password);
    }

    public async Task<IdentityResult> SetLockedAsync(string id, bool locked, string currentAdminId)
    {
        if (id == currentAdminId)
            return Failed("Je kunt jezelf niet blokkeren.");
        using var scope = scopeFactory.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(id);
        if (user is null)
            return Failed("Gebruiker niet gevonden.");
        await um.SetLockoutEnabledAsync(user, true);
        var result = await um.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        // Invalidate existing sessions; the revalidating auth state provider signs them out.
        await um.UpdateSecurityStampAsync(user);
        return result;
    }

    public async Task<IdentityResult> DeleteAsync(string id, string currentAdminId)
    {
        if (id == currentAdminId)
            return Failed("Je kunt jezelf niet verwijderen.");
        using var scope = scopeFactory.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(id);
        if (user is null)
            return Failed("Gebruiker niet gevonden.");
        // Streams of a deleted user stay (owner becomes empty) so running broadcasts are not cut off.
        return await um.DeleteAsync(user);
    }

    private static IdentityResult Failed(string message) =>
        IdentityResult.Failed(new IdentityError { Description = message });
}
