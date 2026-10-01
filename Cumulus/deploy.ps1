param(
    [string]$Source,
    [string]$Destination
)

Copy-Item -LiteralPath $Source -Destination $Destination -Force
Write-Host "Deployed: $Source -> $Destination"
