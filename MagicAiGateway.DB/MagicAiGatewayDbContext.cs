using MagicAiGateway.DB.Contracts;
using MagicAiGateway.DB.Entities;
using Microsoft.EntityFrameworkCore;

namespace MagicAiGateway.DB;

public sealed class MagicAiGatewayDbContext(DbContextOptions<MagicAiGatewayDbContext> options) : DbContext(options)
{
    public DbSet<SecurityPrincipalEntity> SecurityPrincipals => Set<SecurityPrincipalEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<PrincipalRoleEntity> PrincipalRoles => Set<PrincipalRoleEntity>();
    public DbSet<ApiKeyApplicationEntity> ApiKeyApplications => Set<ApiKeyApplicationEntity>();
    public DbSet<SecurityConfigurationEntity> SecurityConfiguration => Set<SecurityConfigurationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("magic_gateway");

        modelBuilder.Entity<SecurityPrincipalEntity>(entity =>
        {
            entity.ToTable("security_principals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PrincipalType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.HasIndex(x => new { x.PrincipalType, x.Name });
        });

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedUsername).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            entity.HasIndex(x => x.PrincipalId).IsUnique();
            entity.HasOne(x => x.Principal).WithOne(x => x.User)
                .HasForeignKey<UserEntity>(x => x.PrincipalId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Prefix).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SecretHash).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.Prefix).IsUnique();
            entity.HasIndex(x => x.PrincipalId).IsUnique();
            entity.HasOne(x => x.Principal).WithOne(x => x.ApiKey)
                .HasForeignKey<ApiKeyEntity>(x => x.PrincipalId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.CreatedByUser).WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RoleEntity>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.BuiltInRole).HasConversion<string>().HasMaxLength(64);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.BuiltInRole).IsUnique();
        });

        modelBuilder.Entity<PrincipalRoleEntity>(entity =>
        {
            entity.ToTable("principal_roles");
            entity.HasKey(x => new { x.PrincipalId, x.RoleId });
            entity.HasOne(x => x.Principal).WithMany(x => x.Roles)
                .HasForeignKey(x => x.PrincipalId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Role).WithMany(x => x.Principals)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AssignedByUser).WithMany()
                .HasForeignKey(x => x.AssignedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ApiKeyApplicationEntity>(entity =>
        {
            entity.ToTable("api_key_applications");
            entity.HasKey(x => new { x.ApiKeyId, x.Application });
            entity.Property(x => x.Application).HasConversion<string>().HasMaxLength(64);
            entity.HasOne(x => x.ApiKey).WithMany(x => x.Applications)
                .HasForeignKey(x => x.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AssignedByUser).WithMany()
                .HasForeignKey(x => x.AssignedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SecurityConfigurationEntity>(entity =>
        {
            entity.ToTable("security_configuration");
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.ActivatedByUser).WithMany()
                .HasForeignKey(x => x.ActivatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
