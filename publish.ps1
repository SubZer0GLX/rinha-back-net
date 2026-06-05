# Builds the linux/amd64 image (with index.bin baked in) and pushes it to GHCR.
#
# Prereqs:
#   - data/references.json.gz, data/normalization.json, data/mcc_risk.json present
#   - a GitHub Personal Access Token with `write:packages` scope
#
# Usage:
#   $env:CR_PAT = "<your-PAT>"
#   ./publish.ps1 -GhcrUser <github-username>

param(
    [Parameter(Mandatory = $true)][string]$GhcrUser,
    [string]$Image = "rinha-2026-fraud",
    [string]$Tag = "latest"
)

$ErrorActionPreference = "Stop"
$ref = "ghcr.io/$GhcrUser/$Image`:$Tag"

if (-not $env:CR_PAT) { throw "Set `$env:CR_PAT to a GitHub PAT with write:packages first." }
$env:CR_PAT | docker login ghcr.io -u $GhcrUser --password-stdin

# build for the test platform (Ubuntu amd64) and push
docker buildx build --platform linux/amd64 -t $ref --push .

Write-Host "Pushed $ref"
Write-Host "Now make the package public in GitHub > your profile > Packages > $Image > Package settings > Change visibility."
Write-Host "And set the image in submission/docker-compose.yml to: $ref"
