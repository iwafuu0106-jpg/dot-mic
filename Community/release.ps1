param([Parameter(Mandatory=$true)][string]$CandidateDirectory,[Parameter(Mandatory=$true)][string]$AcceptancePath,[string]$SourceDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$candidate=(Resolve-Path -LiteralPath $CandidateDirectory).Path
$sourceDirectory=if($SourceDirectory){(Resolve-Path -LiteralPath $SourceDirectory).Path}else{Join-Path $candidate 'source'}
$acceptance=Get-Content -LiteralPath $AcceptancePath -Raw | ConvertFrom-Json
foreach($gate in @('LegacyUnsignedLoad','Init3RtQueueCapx','OrdinaryUi','DiscordUnchangedInput','GainBypassWetOffStop','ManualReboot','Uninstall','VerifyFailureRollback','AssociationLossRepair','ExistingEffectConsentFixture','PermissionConsentFixture','NoPnpProvisioningDependency','MigrationUninstallBaseline')){
 if($acceptance.$gate.Result -ne 'PASS'){throw "Community Release remains incomplete: $gate"}
 $evidence=$acceptance.$gate.Evidence
 if(!$evidence -or !(Test-Path -LiteralPath $evidence)){throw "Missing real gate evidence: $gate"}
 if((Get-FileHash -LiteralPath $evidence).Hash -ne $acceptance.$gate.EvidenceHash){throw "Gate evidence hash mismatch: $gate"}
}
if($acceptance.SecureBoot -ne $true -or $acceptance.MemoryIntegrity -ne $true -or $acceptance.TestSigning -ne $false){throw 'Normal Windows security baseline is not verified'}
$manifest=Get-Content "$candidate/payload.json" -Raw | ConvertFrom-Json
if($manifest.Version -ne '0.4.0-community'){throw 'Wrong Community release line'}
foreach($file in $manifest.Files){$path=[IO.Path]::GetFullPath((Join-Path $candidate $file.Path));if(!$path.StartsWith($candidate+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.Hash){throw 'Frozen candidate payload differs'}}
foreach($path in @('README.md','SECURITY.md','LICENSE','licenses','APO/profile.json')){if(!(Test-Path -LiteralPath (Join-Path $candidate $path))){throw "Distribution material missing: $path"}}
if(!(Test-Path -LiteralPath $sourceDirectory)){throw 'Separate source snapshot missing'}
foreach($directory in @($candidate,$sourceDirectory)){
 $checks=Get-Content (Join-Path $directory 'sha256.json') -Raw | ConvertFrom-Json
 foreach($file in $checks){$path=[IO.Path]::GetFullPath((Join-Path $directory $file.Path));if(!$path.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.Hash){throw 'Frozen binary/source file inventory differs'}}
}
$release=Join-Path $root 'artifacts/apo-community/releases/0.4.0-community.zip'
$source=Join-Path $root 'artifacts/apo-community/releases/0.4.0-community-source.zip'
if((Test-Path $release) -or (Test-Path $source)){throw 'Never overwrite a Community or reference release'}
New-Item -ItemType Directory -Force (Split-Path $release) | Out-Null
# Candidate README remains explicitly pending; release docs must first be updated
# from actual evidence rather than silently rewriting a failed/pending candidate.
if((Get-Content "$candidate/README.md" -Raw -Encoding UTF8) -match '完成Releaseではありません'){throw 'Candidate documentation must be frozen with actual completed-release results before packaging'}
Compress-Archive -LiteralPath $candidate -DestinationPath $release -CompressionLevel Optimal
Compress-Archive -LiteralPath $sourceDirectory -DestinationPath $source -CompressionLevel Optimal
[pscustomobject]@{Version='0.4.0-community';Binary=$release;BinaryHash=(Get-FileHash $release).Hash;Source=$source;SourceHash=(Get-FileHash $source).Hash;CompletedCommunityRelease=$false;ArchiveExtractionAndStartup='PENDING';MicrosoftCertified=$false;AcceptancePath=(Resolve-Path $AcceptancePath).Path} | ConvertTo-Json -Depth 4 | Set-Content artifacts/apo-community/releases/release-packaged.json -Encoding UTF8
