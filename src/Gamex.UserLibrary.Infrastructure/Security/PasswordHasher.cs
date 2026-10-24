using Gamex.UserLibrary.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace Gamex.UserLibrary.Infrastructure.Security;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (versioned format, upgradable) without the Identity tables.</summary>
internal sealed class PasswordHasher : IPasswordHasher
{
    private static readonly object HasherUser = new();
    private readonly PasswordHasher<object> _inner = new();

    public string Hash(string password) => _inner.HashPassword(HasherUser, password);

    public bool Verify(string hash, string password) =>
        _inner.VerifyHashedPassword(HasherUser, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
