namespace MediaMtxPanel.Data;

public static class Roles
{
    /// <summary>Manages all streams and panel users.</summary>
    public const string Admin = "Admin";

    /// <summary>Manages only their own streams.</summary>
    public const string User = "User";

    public static readonly string[] All = [Admin, User];
}
