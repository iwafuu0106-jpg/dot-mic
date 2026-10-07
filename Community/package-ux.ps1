param([Parameter(Mandatory=$true)][string]$BaseDirectory,[string]$OutputDirectory='artifacts/ux1')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $base=(Resolve-Path -LiteralPath $BaseDirectory).Path
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path $output){throw 'Choose a fresh output; never overwrite a frozen distribution'}
 $version='0.4.0-community-ux1'
 $package=Join-Path $output $version
 $internal=Join-Path $package '内部ファイル'
 New-Item -ItemType Directory $internal | Out-Null
 $baseManifest=Get-Content "$base/payload.json" -Raw -Encoding UTF8 | ConvertFrom-Json
 if($baseManifest.Version -ne '0.4.0-community'){throw 'Wrong baseline distribution'}
 foreach($file in $baseManifest.Files){if((Get-FileHash -LiteralPath (Join-Path $base $file.Path)).Hash -ne $file.Hash){throw 'Accepted baseline differs'}}
 Get-ChildItem $base -Force | Copy-Item -Destination $internal -Recurse
 Get-ChildItem $internal -Recurse -File -Force | ForEach-Object {$_.IsReadOnly=$false}
 Native {dotnet publish Community/Setup/DotMic.Setup.csproj -c Release -r win-x64 --self-contained true -o "$output/setup" -p:DebugType=None -p:DebugSymbols=false}
 Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o "$output/ui" -p:Platform=x64 -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false}
 Get-ChildItem "$output/setup" | Copy-Item -Destination $internal -Recurse -Force
 Get-ChildItem "$output/ui" | Copy-Item -Destination "$internal/UI" -Recurse -Force
 Get-ChildItem $internal -Recurse -File -Filter '*.pdb' | Remove-Item
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 Native {& $exe -S Community/Launchers -B "$output/launchers" -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
 Native {& $exe --build "$output/launchers" --config Release --target DotMic.AppLauncher DotMic.SetupLauncher DotMic.LauncherChecks --parallel 2}
 Native {& "$output/launchers/Release/DotMic.LauncherChecks.exe"}
 Copy-Item "$output/launchers/Release/DOT MIC.exe","$output/launchers/Release/セットアップ.exe" $package
 Copy-Item Community/はじめに.txt "$package/はじめに.txt"
 Copy-Item Community/README.md "$internal/README.md" -Force
 '{"Version":"0.4.0-community","Distribution":"0.4.0-community-ux1","Integration":"legacy","PaidSigningRequired":false}' | Set-Content "$internal/UI/community.json" -Encoding UTF8
 $locked=Get-Content Community/Setup/ApoPayload.lock.json -Raw -Encoding UTF8 | ConvertFrom-Json
 foreach($file in $locked){if((Get-FileHash -LiteralPath (Join-Path $internal $file.Path)).Hash -ne $file.Hash){throw 'Production APO/model dependency changed'}}
 # Only the four default CAPX values and UI/entrypoints differ; helper/DSP bytes remain accepted.
 foreach($relative in @('DotMic.Integration.dll','UI/DotMic.Integration.dll','UI/DotMic.ApoSettings.dll')){
  if((Get-FileHash (Join-Path $internal $relative)).Hash -ne (Get-FileHash (Join-Path $base $relative)).Hash){throw 'Native integration/control changed unexpectedly'}
 }
 Remove-Item "$internal/payload.json","$internal/sha256.json"
 $files=@(Get-ChildItem $internal -Recurse -File -Force | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($internal.Length+1).Replace('\','/');Hash=(Get-FileHash $_.FullName).Hash}})
 [pscustomobject]@{Version='0.4.0-community';ApoHash=($locked | Where-Object Path -eq 'APO/DotMic.ApoGate.dll').Hash;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$internal/payload.json" -Encoding UTF8
 $inventory=@(Get-ChildItem $package -Recurse -File -Force | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');Hash=(Get-FileHash $_.FullName).Hash}})
 $inventory | ConvertTo-Json -Depth 4 | Set-Content "$internal/sha256.json" -Encoding UTF8
 Native {dotnet run --project Community/Setup.Tests/DotMic.Setup.Presentation.Tests.csproj -c Release}
 Native {dotnet run --project tests/app-defaults/App.Defaults.Check.csproj -c Release}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [IO.Compression.ZipFile]::CreateFromDirectory($package,"$output/$version.zip",[IO.Compression.CompressionLevel]::Optimal,$false)
 [pscustomobject]@{Distribution=$version;BackendContract='0.4.0-community';Zip="$version.zip";Sha256=(Get-FileHash "$output/$version.zip").Hash;DefaultBypass=$false;DefaultSignInStart=$true;SetupEntry='セットアップ.exe';AppEntry='DOT MIC.exe';FixedProductionFiles=14;ProductionDspModelUnchanged=$true;NativeHelperUnchanged=$true;ActualInstallOrAudioRestartPerformed=$false} | ConvertTo-Json | Set-Content "$output/package-result.json" -Encoding UTF8
 Get-Content "$output/package-result.json" -Raw -Encoding UTF8
}finally{Pop-Location}
