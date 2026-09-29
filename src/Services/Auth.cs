using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Prim.Data;

namespace Prim.Services;

/// <summary>Authentication result.</summary>
public sealed record AuthResult(bool Ok, string UserId, string DisplayName, string Role, string? Error)
{
    public static AuthResult Success(string userId, string displayName, string role)
        => new(true, userId, displayName, role, null);
    public static AuthResult Fail(string error) => new(false, "", "", "", error);
}

/// <summary>
/// Identity provider abstraction. Production uses OAuth/SSO to determine who the
/// user is — that implementation replaces DevPasswordAuthProvider without any
/// other app changes. Password auth must never be hard-wired through the app:
/// always resolve IAuthProvider.
/// </summary>
public interface IAuthProvider
{
    string Name { get; }
    Task<AuthResult> AuthenticateAsync(string userId, string password);
}

/// <summary>PBKDF2-SHA256 password hashing for the dev provider only.</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hashB64, string saltB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Development username+password provider. Seeded users get dev passwords in
/// SeedData. Not for production — production registers an OAuth/SSO IAuthProvider.
/// </summary>
public class DevPasswordAuthProvider : IAuthProvider
{
    private readonly IDbContextFactory<PrimDbContext> _factory;
    public DevPasswordAuthProvider(IDbContextFactory<PrimDbContext> factory) => _factory = factory;
    public string Name => "Development password provider";

    public async Task<AuthResult> AuthenticateAsync(string userId, string password)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password))
            return AuthResult.Fail("Enter a username and password.");
        using var db = _factory.CreateDbContext();
        var u = await db.Users.FirstOrDefaultAsync(x => x.UserId == userId.Trim() && x.Active);
        if (u == null || string.IsNullOrEmpty(u.PasswordHash) || string.IsNullOrEmpty(u.PasswordSalt))
            return AuthResult.Fail("Invalid username or password.");
        if (!PasswordHasher.Verify(password, u.PasswordHash, u.PasswordSalt))
            return AuthResult.Fail("Invalid username or password.");
        return AuthResult.Success(u.UserId, u.DisplayName, u.Role);
    }
}
