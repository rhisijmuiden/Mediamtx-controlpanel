namespace MediaMtxPanel.Data;

public static class Roles
{
    /// <summary>Manages all streams, servers and panel users.</summary>
    public const string Admin = "Admin";

    /// <summary>Manages only their own streams.</summary>
    public const string User = "User";

    /// <summary>
    /// Customer: sees the customer portal with the streams assigned to them (preview, connection
    /// details, statistics) and can sell access to them behind a paywall. Cannot create streams.
    /// </summary>
    public const string Customer = "Customer";

    /// <summary>Roles that use the management screens.</summary>
    public const string Staff = Admin + "," + User;

    public static readonly string[] All = [Admin, User, Customer];

    public static string Label(string role) => role switch
    {
        Admin => "Beheerder",
        Customer => "Klant",
        _ => "Gebruiker",
    };
}
