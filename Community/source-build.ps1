param([string]$OutputDirectory='artifacts/community-source-build', [string]$DependencyRoot=(Join-Path (Split-Path $PSScriptRoot) '.deps'))
& "$PSScriptRoot/build-candidate.ps1" -OutputDirectory $OutputDirectory -DependencyRoot $DependencyRoot
