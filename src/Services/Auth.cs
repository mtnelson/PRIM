using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rim.Data;

namespace Rim.Services;

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
/// Registration is opt-in via Auth:AllowDevPasswords (default false, fail closed).
/// </summary>
public class DevPasswordAuthProvider : IAuthProvider
{
    private readonly IDbContextFactory<RimDbContext> _factory;
    public DevPasswordAuthProvider(IDbContextFactory<RimDbContext> factory) => _factory = factory;
    public string Name => "Development password provider";

    // H6: login-attempt throttling — max 5 failures per rolling minute per
    // username, then a 5-minute lockout. App-wide (static) so it holds across
    // circuits; a successful login clears the entry.
    private static readonly ConcurrentDictionary<string, AttemptState> _attempts = new();
    private const int MaxFailuresPerMinute = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private sealed record AttemptState(int Failures, DateTimeOffset FirstFailureUtc, DateTimeOffset? LockedUntilUtc);

    public async Task<AuthResult> AuthenticateAsync(string userId, string password)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password))
            return AuthResult.Fail("Enter a username and password.");
        var key = userId.Trim().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        if (_attempts.TryGetValue(key, out var state) && state.LockedUntilUtc > now)
            return AuthResult.Fail("Too many failed login attempts. Try again in a few minutes.");
        var result = await AuthenticateCoreAsync(userId, password);
        if (result.Ok)
        {
            _attempts.TryRemove(key, out _);
            return result;
        }
        _attempts.AddOrUpdate(key,
            _ => new AttemptState(1, now, null),
            (_, prev) =>
            {
                var (failures, first) = prev.FirstFailureUtc <= now - AttemptWindow
                    ? (1, now)
                    : (prev.Failures + 1, prev.FirstFailureUtc);
                var lockedUntil = failures >= MaxFailuresPerMinute
                    ? (DateTimeOffset?)(now + LockoutDuration)
                    : null;
                return new AttemptState(failures, first, lockedUntil);
            });
        return result;
    }

    private async Task<AuthResult> AuthenticateCoreAsync(string userId, string password)
    {
        using var db = _factory.CreateDbContext();
        var u = await db.Users.FirstOrDefaultAsync(x => x.UserId == userId.Trim() && x.Active);
        if (u == null || string.IsNullOrEmpty(u.PasswordHash) || string.IsNullOrEmpty(u.PasswordSalt))
            return AuthResult.Fail("Invalid username or password.");
        if (!PasswordHasher.Verify(password, u.PasswordHash, u.PasswordSalt))
            return AuthResult.Fail("Invalid username or password.");
        return AuthResult.Success(u.UserId, u.DisplayName, u.Role);
    }
}

/// <summary>
/// Fail-closed IAuthProvider: registered when Auth:AllowDevPasswords is not
/// enabled. Password login is impossible; production wires an OAuth/SSO
/// provider in its place.
/// </summary>
public class DisabledAuthProvider : IAuthProvider
{
    public string Name => "Disabled (password login not enabled)";

    public Task<AuthResult> AuthenticateAsync(string userId, string password)
        => Task.FromResult(AuthResult.Fail(
            "Password sign-in is disabled on this server. Contact your administrator."));
}
