param([ValidateSet('build','fixtures','candidate')][string]$Action='candidate',[string]$Name='0.4.0-community-01',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $ref=Join-Path $root 'artifacts/apo-community/reference/0.3.4-local'
 $guard=Get-Content "$ref/sha256.json" -Raw | ConvertFrom-Json
 foreach($file in $guard){if((Get-FileHash -LiteralPath (Join-Path $ref $file.Path)).Hash -ne $file.Hash){throw 'Immutable0.3.4 reference changed'}}
 & ./Community/prepare-payload.ps1
 $unsigned=Join-Path $root 'artifacts/apo-community/unsigned-production-payload'
 $pinned=Get-Content Community/Setup/ApoPayload.lock.json -Raw | ConvertFrom-Json
 foreach($file in $pinned){if((Get-FileHash -LiteralPath (Join-Path $unsigned ([IO.Path]::GetFileName($file.Path)))).Hash -ne $file.Hash){throw 'Community fixed unsigned dependency lock mismatch'}}
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 if(($Action -eq 'build' -or $Action -eq 'candidate') -and !$SkipBuild){
   Native {& $exe -S Community -B artifacts/apo-community/native-build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
   Native {& $exe --build artifacts/apo-community/native-build --config Release --parallel 2}
   Native {dotnet publish Community/Setup/DotMic.Setup.csproj -c Release -r win-x64 --self-contained true -o artifacts/apo-community/setup-build}
   Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/apo-community/ui-build -p:Platform=x64 -p:RestoreLockedMode=true}
   Copy-Item artifacts/apo-community/native-build/Release/DotMic.ApoSettings.dll artifacts/apo-community/ui-build
   Copy-Item artifacts/apo-community/native-build/Release/DotMic.Integration.dll artifacts/apo-community/ui-build
   Copy-Item artifacts/apo-community/native-build/Release/DotMic.Integration.dll artifacts/apo-community/setup-build
   '{"Version":"0.4.0-community","Integration":"legacy","PaidSigningRequired":false}' | Set-Content artifacts/apo-community/ui-build/community.json -Encoding UTF8
 }
 if($Action -eq 'fixtures'){
   Native {dotnet artifacts/apo-community/setup-build/DotMic.Setup.dll --fixtures artifacts/apo-community/runs/legacy-20261007-01/setup-fixtures.txt}
   Get-Content artifacts/apo-community/runs/legacy-20261007-01/setup-fixtures.txt
 }
 if($Action -eq 'candidate'){
   if($Name -notmatch '^0\.4\.0-community-[a-zA-Z0-9-]+$'){throw 'Distinct Community candidate name required'}
   $dest=Join-Path $root "artifacts/apo-community/candidates/$Name"
   if(Test-Path $dest){throw 'Never overwrite a candidate or0.3.4 release'}
   New-Item -ItemType Directory "$dest/APO","$dest/UI","$dest/licenses","$dest/source" | Out-Null
   Get-ChildItem artifacts/apo-community/setup-build -File -Recurse | Where-Object {$_.Extension -notin @('.pdb','.log')} | ForEach-Object {$relative=$_.FullName.Substring((Resolve-Path artifacts/apo-community/setup-build).Path.Length+1);$path=Join-Path $dest $relative;New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null;Copy-Item -LiteralPath $_.FullName -Destination $path}
   Copy-Item artifacts/apo-community/ui-build/* "$dest/UI" -Recurse
   Get-ChildItem $unsigned -File | Where-Object {$_.Extension -in @('.dll','.onnx')} | Copy-Item -Destination "$dest/APO"
   Copy-Item "$unsigned/provenance.json" "$dest/unsigned-provenance.json"
   Add-Type -AssemblyName System.IO.Compression.FileSystem
   $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $ref '0.3.4-local.zip'))
   try{foreach($entry in $archive.Entries){$relative=$entry.FullName.Replace('\','/');if($relative -match '^0\.3\.4-local/licenses/([^/]+)$'){[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path "$dest/licenses" $Matches[1]),$false)}elseif($relative -eq '0.3.4-local/APO/profile.json'){[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,"$dest/APO/profile.json",$false)}}}finally{$archive.Dispose()}
    Copy-Item Community/README.md "$dest/README.md"
    $readme=Get-Content "$dest/README.md" -Raw -Encoding UTF8
    "> 開発候補です。完成Releaseではありません。配布ZIPと混同しないでください。`r`n`r`n$readme" | Set-Content "$dest/README.md" -Encoding UTF8
   Copy-Item Community/SECURITY.md "$dest/SECURITY.md"
   Copy-Item LICENSE "$dest/LICENSE"
   foreach($dir in @('Community','App','Apo','ApoGate','Native','docs','licenses','tests')){Get-ChildItem $dir -Recurse -File | Where-Object {$_.FullName -notmatch '\\(bin|obj|build|node_modules)\\'} | ForEach-Object {$relative=$_.FullName.Substring($root.Length+1);$target=Join-Path "$dest/source" $relative;New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null;Copy-Item -LiteralPath $_.FullName -Destination $target}}
   foreach($file in @('LICENSE','README.md','WORKLOG.md','DESIGN.md','CMakeLists.txt','dev.ps1','dependencies.lock.json')){Copy-Item -LiteralPath $file -Destination "$dest/source"}
   $apoHash=(Get-FileHash "$dest/APO/DotMic.ApoGate.dll").Hash
   if($apoHash -ne ($pinned | Where-Object {$_.Path -eq 'APO/DotMic.ApoGate.dll'}).Hash){throw 'Community APO differs from the fixed signature-only derivative'}
   $files=@(Get-ChildItem $dest -Recurse -File | Where-Object {$_.FullName -notmatch '\\source\\' -and $_.FullName -ne [IO.Path]::GetFullPath("$dest/APO/profile.json")} | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($dest.Length+1).Replace('\','/');Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
   [pscustomobject]@{Version='0.4.0-community';ApoHash=$apoHash;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$dest/payload.json" -Encoding UTF8
   [pscustomobject]@{Time=(Get-Date -Format o);Candidate=$dest;CompletedCommunityRelease=$false;ActualLegacyProof='PENDING';DspExecutableImageUnchanged=$true;DevelopmentSignaturesRemoved=$true;NoPnpInstallOrTrustImport=$true} | ConvertTo-Json -Depth 4 | Set-Content artifacts/apo-community/runs/legacy-20261007-01/candidate.json -Encoding UTF8
   Write-Output "Candidate only, not completed Community Release: $dest"
 }
}finally{Pop-Location}
