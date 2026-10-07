param(
  [Parameter(Mandatory=$true)][string]$RunDirectory,
  [Parameter(Mandatory=$true)][string]$UiDirectory,
  [Parameter(Mandatory=$true)][string]$DeliveryDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot
$run=[IO.Path]::GetFullPath($RunDirectory).TrimEnd('\','/');$ui=[IO.Path]::GetFullPath($UiDirectory).TrimEnd('\','/');$delivery=[IO.Path]::GetFullPath($DeliveryDirectory).TrimEnd('\','/')
$runs=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-production/runs'))+'\'
$releases=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-production/releases'))+'\'
if(!$run.StartsWith($runs,[StringComparison]::OrdinalIgnoreCase) -or !$delivery.StartsWith($releases,[StringComparison]::OrdinalIgnoreCase)){throw 'Owned run and fresh production release directory required'}
if((Test-Path $delivery) -or (Test-Path "$delivery.zip")){throw 'Never overwrite existing releases or original ZIPs'}
$extract="$delivery-extracted-check"
if(Test-Path $extract){throw 'Extraction check already exists; never overwrite'}
$state=Get-Content "$run/state.json" -Raw | ConvertFrom-Json
$acceptance=Get-Content "$run/discord-live-nc-01/acceptance.json" -Raw | ConvertFrom-Json
$g=Get-Content "$run/g-result.json" -Raw | ConvertFrom-Json
$phase=Get-Content "$run/phase-result.json" -Raw | ConvertFrom-Json
if(!$state.Installed -or !$acceptance.LegacyRemovalAuthorizedByAcceptance -or !$g.Completed -or !$phase.CompletedProduct){throw 'Required actual acceptance and G completion are not recorded'}
if(!$phase.OwnedTracesStopped -or $phase.GeneralDistributionReady){throw 'Local release only, all owned traces stopped'}
$expected=($state.Files | Where-Object Name -eq 'DotMic.ApoGate.dll').Hash
if($expected -ne $acceptance.ActualSignedDllHash){throw 'Accepted audio payload differs from intended package'}
function VerifyCopy($files,$directory){foreach($file in $files){if((Get-FileHash -LiteralPath (Join-Path $directory $file.Name)).Hash -ne $file.Hash){throw "Package hash mismatch: $($file.Name)"}}}
VerifyCopy $state.Files $state.Package
VerifyCopy $state.Rollback.Files $state.Rollback.Package
$frozen=Get-Content "$ui.freeze.json" -Raw | ConvertFrom-Json
foreach($file in $frozen){if((Get-FileHash -LiteralPath (Join-Path $ui $file.Path)).Hash -ne $file.Hash){throw "UI changed after freeze: $($file.Path)"}}
if(!(Test-Path "$ui/DotMic.ApoSettings.dll") -or (Test-Path "$ui/DotMic.Native.dll")){throw 'CAPX settings DLL required; old engine must not ship'}
New-Item -ItemType Directory $delivery | Out-Null
foreach($name in @('UI','APO','Rollback','docs','evidence','tools')){New-Item -ItemType Directory "$delivery/$name" | Out-Null}
foreach($file in $frozen){$target=Join-Path "$delivery/UI" $file.Path;New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null;Copy-Item -LiteralPath (Join-Path $ui $file.Path) -Destination $target}
foreach($file in $frozen){if((Get-FileHash -LiteralPath (Join-Path "$delivery/UI" $file.Path)).Hash -ne $file.Hash){throw "Copied UI differs from freeze: $($file.Path)"}}
Copy-Item "$($state.Package)/*" "$delivery/APO"
Copy-Item (Join-Path $root 'models/profile.json') "$delivery/APO/profile.json"
Copy-Item "$($state.Rollback.Package)/*" "$delivery/Rollback"
Copy-Item (Join-Path $root 'licenses') "$delivery/licenses" -Recurse
Copy-Item (Join-Path $root 'LICENSE') $delivery
Copy-Item (Join-Path $root 'README.md') $delivery
Copy-Item (Join-Path $root 'docs/apo-production.md'),(Join-Path $root 'docs/apo-release.md') "$delivery/docs"
foreach($name in @('phase-result.json','performance.json','performance-interpretation.json','g-result.json')){Copy-Item "$run/$name" "$delivery/evidence"}
$phase.Release='IMPLEMENTATION_ACCEPTED_PACKAGING_SNAPSHOT_FINAL_ZIP_CHECKS_IN_ORIGINAL_OWNERSHIP_RUN'
$phase | ConvertTo-Json -Depth 10 | Set-Content "$delivery/evidence/phase-result.json" -Encoding UTF8
Copy-Item "$run/discord-live-nc-01/acceptance.json" "$delivery/evidence"
Copy-Item (Join-Path $root 'artifacts/apo-production/build/Release/DotMic.ApoControl.exe') "$delivery/tools"
VerifyCopy $state.Files "$delivery/APO"
VerifyCopy $state.Rollback.Files "$delivery/Rollback"
[pscustomobject]@{Created=(Get-Date -Format o);Name='DOT MIC local APO Release';ApoVersion=$state.Version;UiAssemblyVersion='3.2.0';CompletedProduct=$true;GenerallyDistributable=$false;Target='USB\VID_3142&PID_00C1&MI_00';ExpectedApoHash=$expected;OwnershipRun=$run;ReceiptPolicy='Use original latest workspace receipt, never copied evidence';RollbackVersion=$state.Rollback.Version;CertExpires='2026-11-06';OldBridgeRemoved=$true;SharedModelPreprocessingUnchanged=$true;UnrelatedFixedSrcRetained=$true;PerformanceOptimizationNeeded=$false;PcmRecorded=$false} | ConvertTo-Json -Depth 5 | Set-Content "$delivery/manifest.json" -Encoding UTF8
$hashes=@(Get-ChildItem $delivery -Recurse -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($delivery.Length+1);Bytes=$_.Length;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$hashes | ConvertTo-Json -Depth 4 | Set-Content "$delivery/sha256.json" -Encoding UTF8
$hashManifest=(Get-FileHash -LiteralPath "$delivery/sha256.json").Hash
Compress-Archive -LiteralPath $delivery -DestinationPath "$delivery.zip" -CompressionLevel Optimal
Expand-Archive -LiteralPath "$delivery.zip" -DestinationPath $extract
$payload=Join-Path $extract (Split-Path $delivery -Leaf)
foreach($entry in $hashes){if((Get-FileHash -LiteralPath (Join-Path $payload $entry.Path)).Hash -ne $entry.Hash){throw "Extracted ZIP hash mismatch: $($entry.Path)"}}
foreach($file in $frozen){if((Get-FileHash -LiteralPath (Join-Path "$payload/UI" $file.Path)).Hash -ne $file.Hash){throw "Extracted UI differs from freeze: $($file.Path)"}}
if((Get-FileHash -LiteralPath "$payload/sha256.json").Hash -ne $hashManifest){throw 'Extracted hash manifest differs'}
[pscustomobject]@{Time=(Get-Date -Format o);Path="$delivery.zip";Bytes=(Get-Item "$delivery.zip").Length;Hash=(Get-FileHash "$delivery.zip").Hash;AllPayloadHashesVerified=$true;CopiedAndExtractedUiMatchesFreeze=$true;ExtractedUi=(Join-Path $payload 'UI/DotMic.App.exe');StartupVerification='PENDING_EXTRACTED_UI_STARTUP';CompletedProduct=$true;GenerallyDistributable=$false} | ConvertTo-Json -Depth 4 | Set-Content "$run/release-final.json" -Encoding UTF8
Write-Output "PASS fresh local Release ZIP and extracted payload hashes: $delivery.zip"
