param([string]$OutputDirectory='artifacts/community-source-build')
# Builds portable Community UI/Setup/control only, without machine integration.
# The accepted DSP/model payload remains independently pinned, never rebuilt here.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $payload=Join-Path $root 'reference-payload'
 $lock=Get-Content Community/Setup/ApoPayload.lock.json -Raw -Encoding UTF8 | ConvertFrom-Json
 foreach($file in $lock){if((Get-FileHash -LiteralPath (Join-Path $payload ([IO.Path]::GetFileName($file.Path)))).Hash -ne $file.Hash){throw 'Pinned accepted payload hash mismatch'}}
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path $output){throw 'Choose a fresh source-build output'}
 $internal=Join-Path $output '内部ファイル'
 & ./ApoGate/gate.ps1 restore
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 $build=Join-Path $output 'native-build'
 Native {& $exe -S Community -B $build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
 Native {& $exe --build $build --config Release --parallel 2}
 Native {dotnet publish Community/Setup/DotMic.Setup.csproj -c Release -r win-x64 --self-contained true -o $internal -p:DebugType=None -p:DebugSymbols=false}
 Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o "$internal/UI" -p:Platform=x64 -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false}
 Copy-Item "$build/Release/DotMic.Integration.dll" $internal
 Copy-Item "$build/Release/DotMic.Integration.dll" "$internal/UI"
 Copy-Item "$build/Release/DotMic.ApoSettings.dll" "$internal/UI"
 '{"Version":"0.4.0-community","Distribution":"0.4.0-community-ux1","Integration":"legacy","PaidSigningRequired":false}' | Set-Content "$internal/UI/community.json" -Encoding UTF8
 New-Item -ItemType Directory "$internal/APO" | Out-Null
 Get-ChildItem $payload -File | Where-Object {$_.Extension -in @('.dll','.onnx')} | Copy-Item -Destination "$internal/APO"
 Native {& $exe -S Community/Launchers -B "$output/launchers" -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
 Native {& $exe --build "$output/launchers" --config Release --target DotMic.AppLauncher DotMic.SetupLauncher --parallel 2}
 Copy-Item "$output/launchers/Release/DOT MIC.exe","$output/launchers/Release/セットアップ.exe" $output
 Copy-Item Community/はじめに.txt "$output/はじめに.txt"
 $files=@(Get-ChildItem $internal -Recurse -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($internal.Length+1).Replace('\','/');Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
 [pscustomobject]@{Version='0.4.0-community';ApoHash=($lock | Where-Object Path -eq 'APO/DotMic.ApoGate.dll').Hash;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$internal/payload.json" -Encoding UTF8
 Native {dotnet "$internal/DotMic.Setup.dll" --fixtures "$output/source-build-fixtures.txt"}
 [pscustomobject]@{Time=(Get-Date -Format o);Output=$output;PinnedDspModelUnchanged=$true;RegistryAssociationWrites=$false;ServicesRestarted=$false;SourceBuildOnly=$true;AcceptedReleaseReplacement=$false} | ConvertTo-Json | Set-Content "$output/build-result.json" -Encoding UTF8
}finally{Pop-Location}
