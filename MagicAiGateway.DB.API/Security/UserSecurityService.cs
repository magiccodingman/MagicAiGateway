using MagicAiGateway.DB.Contracts;
using MagicAiGateway.DB.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MagicAiGateway.DB.API.Security;

public sealed class UserSecurityService(
    IDbContextFactory<MagicAiGateway.DB.MagicAiGatewayDbContext> contextFactory,
    IPasswordHasher<UserEntity> passwordHasher)
{
    public async Task<AdministratorBootstrapStatusResponse> GetBootstrapStatusAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var administrator = await context.Users
            .AsNoTracking()
            .Include(user => user.Principal)
            .ThenInclude(principal => principal.Roles)
            .ThenInclude(link => link.Role)
            .FirstOrDefaultAsync(user => user.Principal.Roles.Any(link => link.Role.BuiltInRole == BuiltInRole.Administrator), cancellationToken)
            .ConfigureAwait(false);
        var configuration = await context.SecurityConfiguration.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
        return new AdministratorBootstrapStatusResponse(
            administrator is not null,
            administrator?.MustChangePassword ?? false,
            configuration.ApplicationSecurityEnabled);
    }

    public async Task<UserLoginResponse> LoginAsync(UserLoginRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var normalized = SecurityBootstrapper.Normalize(request.Username);
        var user = await context.Users
            .Include(x => x.Principal)
            .ThenInclude(x => x.Roles)
            .ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken)
            .ConfigureAwait(false);
        if (user is null || user.Principal.DisabledAt is not null)
        {
            return new UserLoginResponse(false, null, null, false, [], "Invalid username or password.");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return new UserLoginResponse(false, null, null, false, [], "Invalid username or password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var roles = BuiltInRoleHierarchy.Expand(user.Principal.Roles
            .Where(link => link.Role.BuiltInRole != null)
            .Select(link => link.Role.BuiltInRole!.Value));
        return new UserLoginResponse(
            true,
            user.Id,
            user.Username,
            user.MustChangePassword,
            roles.OrderBy(static role => role).ToArray());
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ValidateNewPassword(request.NewPassword);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var normalized = SecurityBootstrapper.Normalize(request.Username);
        var user = await context.Users
            .Include(x => x.Principal)
            .SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Invalid username or password.");
        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.SecurityStamp = Guid.NewGuid();
        var configuration = await context.SecurityConfiguration.SingleAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
        configuration.SecurityRevision++;
        user.Principal.SecurityRevision = configuration.SecurityRevision;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAdministratorPasswordAsync(string newTemporaryPassword, CancellationToken cancellationToken)
    {
        ValidateNewPassword(newTemporaryPassword);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var administrator = await context.Users
            .Include(user => user.Principal)
            .ThenInclude(principal => principal.Roles)
            .ThenInclude(link => link.Role)
            .SingleAsync(user => user.Principal.Roles.Any(link => link.Role.BuiltInRole == BuiltInRole.Administrator), cancellationToken)
            .ConfigureAwait(false);
        administrator.PasswordHash = passwordHasher.HashPassword(administrator, newTemporaryPassword);
        administrator.MustChangePassword = true;
        administrator.PasswordChangedAt = null;
        administrator.UpdatedAt = DateTimeOffset.UtcNow;
        administrator.SecurityStamp = Guid.NewGuid();
        var configuration = await context.SecurityConfiguration.SingleAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
        configuration.SecurityRevision++;
        administrator.Principal.SecurityRevision = configuration.SecurityRevision;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new ArgumentException("Passwords must contain at least 12 characters.", nameof(password));
        }
    }
}

public sealed class AdminRecoveryGate
{
    private int _used;
    public bool TryUse() => Interlocked.Exchange(ref _used, 1) == 0;
}
