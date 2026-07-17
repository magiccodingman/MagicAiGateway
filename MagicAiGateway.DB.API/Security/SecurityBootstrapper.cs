using MagicAiGateway.DB.API.Configuration;
using MagicAiGateway.DB.Contracts;
using MagicAiGateway.DB.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MagicAiGateway.DB.API.Security;

public sealed class SecurityBootstrapper(
    IDbContextFactory<MagicAiGateway.DB.MagicAiGatewayDbContext> contextFactory,
    IPasswordHasher<UserEntity> passwordHasher,
    IOptions<InitialAdministratorOptions> administratorOptions,
    ILogger<SecurityBootstrapper> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var configuration = await context.SecurityConfiguration.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            context.SecurityConfiguration.Add(new SecurityConfigurationEntity
            {
                Id = 1,
                ApplicationSecurityEnabled = false,
                SecurityRevision = 0
            });
        }

        var administratorRole = await context.Roles.SingleOrDefaultAsync(
            role => role.BuiltInRole == BuiltInRole.Administrator,
            cancellationToken).ConfigureAwait(false);
        if (administratorRole is null)
        {
            administratorRole = new RoleEntity
            {
                Id = SecuritySeedIds.AdministratorRole,
                Name = nameof(BuiltInRole.Administrator),
                Description = "Full platform administrator.",
                BuiltInRole = BuiltInRole.Administrator,
                CreatedAt = now
            };
            context.Roles.Add(administratorRole);
        }

        var normalized = Normalize(administratorOptions.Value.Username);
        var existingAdministrator = await context.Users
            .Include(user => user.Principal)
            .ThenInclude(principal => principal.Roles)
            .SingleOrDefaultAsync(user => user.NormalizedUsername == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (existingAdministrator is null)
        {
            if (string.IsNullOrWhiteSpace(administratorOptions.Value.Password))
            {
                throw new InvalidOperationException(
                    "The initial administrator does not exist and Security:InitialAdministrator:Password was not supplied.");
            }

            var principal = new SecurityPrincipalEntity
            {
                Id = Guid.NewGuid(),
                PrincipalType = SecurityPrincipalType.User,
                Name = administratorOptions.Value.Username,
                Description = "Built-in administrator account.",
                CreatedAt = now,
                SecurityRevision = 0
            };
            var user = new UserEntity
            {
                Id = Guid.NewGuid(),
                PrincipalId = principal.Id,
                Principal = principal,
                Username = administratorOptions.Value.Username,
                NormalizedUsername = normalized,
                MustChangePassword = true,
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now
            };
            user.PasswordHash = passwordHasher.HashPassword(user, administratorOptions.Value.Password);

            context.SecurityPrincipals.Add(principal);
            context.Users.Add(user);
            context.PrincipalRoles.Add(new PrincipalRoleEntity
            {
                PrincipalId = principal.Id,
                RoleId = administratorRole.Id,
                AssignedAt = now
            });
            logger.LogWarning(
                "Created initial administrator {Username}. The configured bootstrap password must be changed before normal administration is allowed.",
                user.Username);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string Normalize(string username) => username.Trim().ToUpperInvariant();
}
