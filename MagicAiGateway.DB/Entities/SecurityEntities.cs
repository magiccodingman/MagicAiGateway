using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Entities;

public sealed class SecurityPrincipalEntity
{
    public Guid Id { get; set; }
    public SecurityPrincipalType PrincipalType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public long SecurityRevision { get; set; }
    public UserEntity? User { get; set; }
    public ApiKeyEntity? ApiKey { get; set; }
    public ICollection<PrincipalRoleEntity> Roles { get; set; } = [];
}

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public SecurityPrincipalEntity Principal { get; set; } = null!;
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; }
    public Guid SecurityStamp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
}

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public SecurityPrincipalEntity Principal { get; set; } = null!;
    public string Prefix { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public UserEntity? CreatedByUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public ICollection<ApiKeyApplicationEntity> Applications { get; set; } = [];
}

public sealed class RoleEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public BuiltInRole? BuiltInRole { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<PrincipalRoleEntity> Principals { get; set; } = [];
}

public sealed class PrincipalRoleEntity
{
    public Guid PrincipalId { get; set; }
    public SecurityPrincipalEntity Principal { get; set; } = null!;
    public Guid RoleId { get; set; }
    public RoleEntity Role { get; set; } = null!;
    public DateTimeOffset AssignedAt { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public UserEntity? AssignedByUser { get; set; }
}

public sealed class ApiKeyApplicationEntity
{
    public Guid ApiKeyId { get; set; }
    public ApiKeyEntity ApiKey { get; set; } = null!;
    public MagicApplication Application { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public UserEntity? AssignedByUser { get; set; }
}

public sealed class SecurityConfigurationEntity
{
    public int Id { get; set; }
    public bool ApplicationSecurityEnabled { get; set; }
    public long SecurityRevision { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public Guid? ActivatedByUserId { get; set; }
    public UserEntity? ActivatedByUser { get; set; }
}
