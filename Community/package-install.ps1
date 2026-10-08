param([Parameter(Mandatory=$true)][string]$BaseDirectory,
 [Parameter(Mandatory=$true)][string]$NativeDirectory,
 [Parameter(Mandatory=$true)][string]$CandidateApo,
 [string]$CandidateLock='Community/Setup/ApoCandidate.lock.json',
 [string]$OutputDirectory='artifacts/install-0.5.0-rc.1')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $base=(Resolve-Path -LiteralPath $BaseDirectory).Path
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path $output){throw 'Choose a fresh output; never replace a frozen release'}
 $payload=Join-Path $output 'payload'
 New-Item -ItemType Directory $payload | Out-Null
  $baseline=Get-Content "$base/payload.json" -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach($file in $baseline.Files){if((Get-FileHash -LiteralPath (Join-Path $base $file.Path)).Hash -ne $file.Hash){throw 'Accepted baseline differs'}}
  $native=(Resolve-Path -LiteralPath $NativeDirectory).Path
  $candidateApoPath=(Resolve-Path -LiteralPath $CandidateApo).Path
  $candidateLockPath=(Resolve-Path -LiteralPath $CandidateLock).Path
  $locked=Get-Content -LiteralPath $candidateLockPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $accepted=Get-Content Community/Setup/ApoPayload.lock.json -Raw -Encoding UTF8 | ConvertFrom-Json
  if($locked.Count -ne $accepted.Count){throw 'Candidate payload scope differs'}
  foreach($file in $accepted){
   $match=@($locked | Where-Object Path -eq $file.Path)
   if($match.Count -ne 1 -or ($file.Path -ne 'APO/DotMic.ApoGate.dll' -and $match[0].Hash -ne $file.Hash)){throw 'Inference/model/runtime payload must remain accepted'}
  }
  if((Get-FileHash -LiteralPath $candidateApoPath).Hash -ne ($locked | Where-Object Path -eq 'APO/DotMic.ApoGate.dll').Hash){throw 'Candidate APO does not match its pinned build lock'}
  foreach($name in @('DotMic.Integration.dll','DotMic.ApoSettings.dll')){if(!(Test-Path -LiteralPath (Join-Path $native $name))){throw "Candidate bridge missing: $name"}}
 Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o "$payload/UI" -p:Platform=x64 -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false}
  Native {dotnet publish Community/Setup/DotMic.Setup.csproj -c Release -r win-x64 --self-contained true -o "$output/setup-runtime" -p:DebugType=None -p:DebugSymbols=false "-p:ApoCandidateLockFile=$candidateLockPath"}
 foreach($file in Get-ChildItem "$output/setup-runtime" -Recurse -File){
  $relative=$file.FullName.Substring((Join-Path $output 'setup-runtime').Length+1)
  $target=Join-Path "$payload/UI" $relative
  if(Test-Path $target){if((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash){
   # Desktop framework forwarding facades are supersets of the core-only facades.
   if($relative -in @('Microsoft.VisualBasic.dll','System.Drawing.dll')){Copy-Item $file.FullName $target -Force}
   else{throw "Conflicting shared runtime file: $relative"}
  }}
  else{New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null;Copy-Item $file.FullName $target}
 }
  New-Item -ItemType Directory "$payload/APO" | Out-Null
  foreach($file in $locked){
   $origin=if($file.Path -eq 'APO/DotMic.ApoGate.dll'){$candidateApoPath}else{Join-Path $base $file.Path}
   Copy-Item -LiteralPath $origin -Destination (Join-Path $payload $file.Path)
  }
 Copy-Item "$base/licenses" $payload -Recurse
 Copy-Item "$base/LICENSE" "$payload/LICENSE"
 Copy-Item Community/SECURITY.md "$payload/SECURITY.md"
  Copy-Item "$native/DotMic.Integration.dll" $payload
  Copy-Item "$native/DotMic.Integration.dll","$native/DotMic.ApoSettings.dll" "$payload/UI"
  '{"Version":"0.4.0-community","Distribution":"0.5.0-rc.1","Integration":"legacy-fleet","PaidSigningRequired":false}' | Set-Content "$payload/UI/community.json" -Encoding UTF8
  '{"Version":"0.5.0-rc.1","BackendContract":"0.4.0-community"}' | Set-Content "$payload/UI/installation-package.json" -Encoding UTF8
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 Native {& $exe -S Community/Launchers -B "$output/launchers" -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0' '-DDOTMIC_INSTALLED_SETUP=ON'}
 Native {& $exe --build "$output/launchers" --config Release --target DotMic.AppLauncher DotMic.SetupLauncher --parallel 2}
  foreach($file in $locked){if((Get-FileHash -LiteralPath (Join-Path $payload $file.Path)).Hash -ne $file.Hash){throw 'Pinned candidate APO/model differs'}}
 Get-ChildItem $payload -Recurse -File -Filter '*.pdb' | Remove-Item
 $files=@(Get-ChildItem $payload -Recurse -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($payload.Length+1).Replace('\','/');Hash=(Get-FileHash $_.FullName).Hash}})
 $entries=@(Get-Item "$output/launchers/Release/DOT MIC.exe","$output/launchers/Release/セットアップ.exe" | ForEach-Object {[pscustomobject]@{Path=$_.Name;Hash=(Get-FileHash $_.FullName).Hash}})
 [pscustomobject]@{Version='0.4.0-community';ApoHash=($locked | Where-Object Path -eq 'APO/DotMic.ApoGate.dll').Hash;Files=$files;ApplicationEntries=$entries} | ConvertTo-Json -Depth 5 | Set-Content "$payload/payload.json" -Encoding UTF8
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $distribution=Join-Path $output 'distribution'
 New-Item -ItemType Directory $distribution | Out-Null
 Copy-Item "$output/launchers/Release/DOT MIC.exe","$output/launchers/Release/セットアップ.exe" $distribution
 Copy-Item $payload "$distribution/内部ファイル" -Recurse
 Copy-Item Community/インストール.txt "$distribution/インストール.txt"
  $zip=Join-Path $output 'DOT MIC 0.5.0-rc.1.zip'
 [IO.Compression.ZipFile]::CreateFromDirectory($distribution,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
  [pscustomobject]@{Version='0.5.0-rc.1';CandidateOnly=$true;ZipName=[IO.Path]::GetFileName($zip);ZipBytes=(Get-Item $zip).Length;ZipFiles=(Get-ChildItem $distribution -File -Recurse).Count;ZipSha256=(Get-FileHash $zip).Hash;PayloadFiles=$files.Count;SingleExecutableBundle=$false;ApoChanged=$true;InferenceModelRuntimeUnchanged=$true;BackendContract='0.4.0-community';DefaultApplicationDirectory='Program Files/DOT MIC/Application';ActualInstallationPerformed=$false;ManagerLifecycleVerified=$false;RealMultiMicrophoneVerified=$false;Published=$false} | ConvertTo-Json | Set-Content "$output/package-result.json" -Encoding UTF8
 Get-Content "$output/package-result.json" -Raw -Encoding UTF8
}finally{Pop-Location}
