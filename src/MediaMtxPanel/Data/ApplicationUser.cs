using Microsoft.AspNetCore.Identity;

namespace MediaMtxPanel.Data;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StreamDefinition> Streams { get; set; } = [];
}
