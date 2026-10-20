using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

/// <summary>One-time code mailed to a viewer to sign in to "My tickets". Only a hash of the code is stored.</summary>
public class ViewerLoginCode
{
    public int Id { get; set; }

    /// <summary>Lowercase e-mail address.</summary>
    [MaxLength(256)]
    public string Email { get; set; } = "";

    [MaxLength(64)]
    public string CodeHash { get; set; } = "";

    public int Attempts { get; set; }

    public bool Used { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
