using Microsoft.AspNetCore.Identity;

namespace MediaMtxPanel.Services.Localization;

/// <summary>Identity's error messages (password rules, duplicate email, …) in the visitor's language.</summary>
public sealed class LocalizedIdentityErrorDescriber(TextLocalizer l) : IdentityErrorDescriber
{
    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };

    public override IdentityError DefaultError() => Error(nameof(DefaultError), l["An unknown failure has occurred."]);
    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), l["Incorrect password."]);
    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), l["Invalid token."]);
    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), l["Email '{0}' is invalid.", email]);
    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), l["Username '{0}' is invalid.", userName]);
    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), l["Email '{0}' is already taken.", email]);
    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), l["Username '{0}' is already taken.", userName]);
    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), l["Passwords must be at least {0} characters.", length]);
    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), l["Passwords must have at least one non alphanumeric character."]);
    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), l["Passwords must have at least one digit ('0'-'9')."]);
    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), l["Passwords must have at least one lowercase ('a'-'z')."]);
    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), l["Passwords must have at least one uppercase ('A'-'Z')."]);
}
