using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MagicAiGateway.DB.Migrations;

[DbContext(typeof(MagicAiGatewayDbContext))]
[Migration("20260717190000_InitialSecurityFoundation")]
public partial class InitialSecurityFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "magic_gateway");

        migrationBuilder.CreateTable(
            name: "roles",
            schema: "magic_gateway",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                BuiltInRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_roles", x => x.Id));

        migrationBuilder.CreateTable(
            name: "security_principals",
            schema: "magic_gateway",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SecurityRevision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_security_principals", x => x.Id));

        migrationBuilder.CreateTable(
            name: "users",
            schema: "magic_gateway",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                NormalizedUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                SecurityStamp = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PasswordChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.Id);
                table.ForeignKey("FK_users_security_principals_PrincipalId", x => x.PrincipalId, "magic_gateway", "security_principals", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "api_keys",
            schema: "magic_gateway",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Prefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SecretHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_keys", x => x.Id);
                table.ForeignKey("FK_api_keys_security_principals_PrincipalId", x => x.PrincipalId, "magic_gateway", "security_principals", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_api_keys_users_CreatedByUserId", x => x.CreatedByUserId, "magic_gateway", "users", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "principal_roles",
            schema: "magic_gateway",
            columns: table => new
            {
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_principal_roles", x => new { x.PrincipalId, x.RoleId });
                table.ForeignKey("FK_principal_roles_roles_RoleId", x => x.RoleId, "magic_gateway", "roles", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_principal_roles_security_principals_PrincipalId", x => x.PrincipalId, "magic_gateway", "security_principals", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_principal_roles_users_AssignedByUserId", x => x.AssignedByUserId, "magic_gateway", "users", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "security_configuration",
            schema: "magic_gateway",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                ApplicationSecurityEnabled = table.Column<bool>(type: "boolean", nullable: false),
                SecurityRevision = table.Column<long>(type: "bigint", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ActivatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_security_configuration", x => x.Id);
                table.ForeignKey("FK_security_configuration_users_ActivatedByUserId", x => x.ActivatedByUserId, "magic_gateway", "users", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "api_key_applications",
            schema: "magic_gateway",
            columns: table => new
            {
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Application = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_key_applications", x => new { x.ApiKeyId, x.Application });
                table.ForeignKey("FK_api_key_applications_api_keys_ApiKeyId", x => x.ApiKeyId, "magic_gateway", "api_keys", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_api_key_applications_users_AssignedByUserId", x => x.AssignedByUserId, "magic_gateway", "users", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex("IX_api_key_applications_AssignedByUserId", "magic_gateway", "api_key_applications", "AssignedByUserId");
        migrationBuilder.CreateIndex("IX_api_keys_CreatedByUserId", "magic_gateway", "api_keys", "CreatedByUserId");
        migrationBuilder.CreateIndex("IX_api_keys_Prefix", "magic_gateway", "api_keys", "Prefix", unique: true);
        migrationBuilder.CreateIndex("IX_api_keys_PrincipalId", "magic_gateway", "api_keys", "PrincipalId", unique: true);
        migrationBuilder.CreateIndex("IX_principal_roles_AssignedByUserId", "magic_gateway", "principal_roles", "AssignedByUserId");
        migrationBuilder.CreateIndex("IX_principal_roles_RoleId", "magic_gateway", "principal_roles", "RoleId");
        migrationBuilder.CreateIndex("IX_roles_BuiltInRole", "magic_gateway", "roles", "BuiltInRole", unique: true);
        migrationBuilder.CreateIndex("IX_roles_Name", "magic_gateway", "roles", "Name", unique: true);
        migrationBuilder.CreateIndex("IX_security_configuration_ActivatedByUserId", "magic_gateway", "security_configuration", "ActivatedByUserId");
        migrationBuilder.CreateIndex(
            name: "IX_security_principals_PrincipalType_Name",
            schema: "magic_gateway",
            table: "security_principals",
            columns: new[] { "PrincipalType", "Name" });
        migrationBuilder.CreateIndex("IX_users_NormalizedUsername", "magic_gateway", "users", "NormalizedUsername", unique: true);
        migrationBuilder.CreateIndex("IX_users_PrincipalId", "magic_gateway", "users", "PrincipalId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("api_key_applications", "magic_gateway");
        migrationBuilder.DropTable("principal_roles", "magic_gateway");
        migrationBuilder.DropTable("security_configuration", "magic_gateway");
        migrationBuilder.DropTable("api_keys", "magic_gateway");
        migrationBuilder.DropTable("roles", "magic_gateway");
        migrationBuilder.DropTable("users", "magic_gateway");
        migrationBuilder.DropTable("security_principals", "magic_gateway");
    }
}
