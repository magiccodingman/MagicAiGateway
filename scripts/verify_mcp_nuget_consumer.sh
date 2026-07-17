#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <package-source-directory>" >&2
  exit 2
fi

package_source="$(realpath "$1")"
package_file="$(find "$package_source" -maxdepth 1 -type f -name 'MagicAiGateway.MCP.Package.*.nupkg' ! -name '*.symbols.nupkg' -print -quit)"
if [[ -z "$package_file" ]]; then
  echo "MagicAiGateway.MCP.Package nupkg was not found in $package_source" >&2
  exit 1
fi

package_name="$(basename "$package_file")"
package_version="${package_name#MagicAiGateway.MCP.Package.}"
package_version="${package_version%.nupkg}"

work_directory="$(mktemp -d)"
trap 'rm -rf "$work_directory"' EXIT

cat > "$work_directory/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$package_source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

cat > "$work_directory/NuGetMcpConsumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <NativeLib>Shared</NativeLib>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MagicAiGateway.MCP.Package" Version="$package_version" />
  </ItemGroup>
</Project>
EOF

cat > "$work_directory/Package.cs" <<'EOF'
using MagicAiGateway.MCP.Package;
using ModelContextProtocol.Server;

public static class PackageProgram
{
    [MagicMcpPackage]
    public static void Configure(MagicMcpPackageBuilder builder)
    {
        builder.Package.ConfigureManifest(manifest =>
        {
            manifest.Name = "NuGet MCP Consumer";
            manifest.Version = "1.0.0";
        });

        builder.AddMcpTools();
    }
}

[McpServerToolType]
public sealed class ConsumerTools : MagicMcpToolController
{
    [McpServerTool(Name = "consumer_ping")]
    public string Ping() => $"pong from {PackageInstance.InstanceId}";
}
EOF

pushd "$work_directory" >/dev/null

dotnet restore \
  --runtime linux-x64 \
  --configfile NuGet.Config

dotnet publish \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --no-restore \
  --output publish

library="$work_directory/publish/NuGetMcpConsumer.so"
test -f "$library"

nm -D --defined-only "$library" |
  awk '{ name = $NF; sub(/@@.*/, "", name); print name }' |
  sort -u > nuget-consumer-exports.txt

for symbol in \
  magic_mcp_get_abi_version \
  magic_mcp_get_manifest \
  magic_mcp_start_instance \
  magic_mcp_send \
  magic_mcp_receive \
  magic_mcp_stop_instance \
  magic_mcp_list_instances \
  magic_mcp_shutdown \
  magic_mcp_get_last_error
do
  grep -Fx "$symbol" nuget-consumer-exports.txt
done

popd >/dev/null

echo "Packed NuGet consumer publish passed for $package_name"
