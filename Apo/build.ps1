param([ValidateSet('build','check','stage')][string]$Action='build',[string]$RunDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
  function Native([scriptblock]$command){ & $command; if($LASTEXITCODE -ne 0){throw "Command failed: $LASTEXITCODE"} }
  $cmake=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
  $build=Join-Path $root 'artifacts/apo-production/build'
  $release=Join-Path $build 'Release'
  $payload=@('DotMic.ApoGate.dll','DotMic.Inference.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll','vccorlib140.dll','dpdfnet2_48khz_hr.onnx')
  if($Action -eq 'build'){
    Native {& $cmake -S Apo -B $build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
    Native {& $cmake --build $build --config Release --parallel 2}
  }elseif($Action -eq 'check'){
    Native {& "$release/DotMic.ApoCheck.exe"}
    Native {& "$release/DotMic.ApoControl.exe" exercise "$release/DotMic.ApoGate.dll"}
  }else{
    if(!$RunDirectory){throw 'A new RunDirectory is required'}
    $run=[IO.Path]::GetFullPath($RunDirectory)
    $allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-production/runs'))+'\'
    if(!$run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $run)){throw 'Choose a new child of artifacts/apo-production/runs'}
    New-Item -ItemType Directory $run | Out-Null
    $package=Join-Path $run 'package';New-Item -ItemType Directory $package | Out-Null
    Copy-Item Apo/inf/*.inf $package
    foreach($name in $payload){Copy-Item (Join-Path $release $name) $package}
    Native {& "$release/DotMic.ApoCheck.exe" | Tee-Object "$run/shell-tests.txt"}
    Native {& "$release/DotMic.ApoControl.exe" exercise "$package/DotMic.ApoGate.dll" | Tee-Object "$run/dll-tests.txt"}
    $kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
    $sign=Join-Path $kit 'bin/10.0.19041.0/x64/signtool.exe'
    $thumb='7C11C359D556261E70B71829B497ADEC3BBD3C20'
    if(!(Test-Path "Cert:\CurrentUser\My\$thumb")){throw 'Existing development certificate unavailable; do not buy/create/install trust implicitly'}
    if((Get-Item "Cert:\CurrentUser\My\$thumb").NotAfter -lt (Get-Date)){throw 'Development certificate expired'}
    foreach($dll in Get-ChildItem $package -Filter '*.dll'){
      # Preserve valid Microsoft signatures. Sign only owned candidate copies
      # of unsigned binaries; never change .deps or existing installation files.
      $signature=Get-AuthenticodeSignature $dll.FullName
      if($signature.Status -eq 'NotSigned'){
        Native {& $sign sign /v /fd sha256 /s My /sha1 $thumb $dll.FullName | Tee-Object "$run/sign-$($dll.Name).txt"}
      }elseif($signature.Status -ne 'Valid'){throw "Invalid existing dependency signature: $($dll.Name)"}
      Native {& $sign verify /pa /v $dll.FullName | Tee-Object "$run/verify-$($dll.Name).txt"}
    }
    Native {& "$kit/Tools/10.0.28000.0/x64/infverif.exe" /w "$package/DotMic.ApoGate.inf" "$package/DotMic.Fifine.Extension.inf" | Tee-Object "$run/infverif.txt"}
    Native {& "$kit/bin/10.0.28000.0/x86/Inf2Cat.exe" "/driver:$package" '/os:10_NI_X64,10_GE_X64' /uselocaltime | Tee-Object "$run/inf2cat.txt"}
    foreach($cat in Get-ChildItem $package -Filter '*.cat'){
      Native {& $sign sign /v /fd sha256 /s My /sha1 $thumb $cat.FullName | Tee-Object "$run/sign-$($cat.BaseName).txt"}
      Native {& $sign verify /pa /v $cat.FullName | Tee-Object "$run/verify-$($cat.BaseName).txt"}
    }
    $version=[regex]::Match((Get-Content "$package/DotMic.ApoGate.inf" -Raw),'DriverVer=[^,\r\n]+,([^\r\n]+)').Groups[1].Value
    if(!$version){throw 'Component version unavailable'}
    [pscustomobject]@{Time=(Get-Date -Format o);Purpose='LOCAL DEVELOPMENT PRODUCTION NC WORKER; ACCEPTANCE PENDING / NOT GENERALLY DISTRIBUTABLE';Version=$version;Package=$package;Thumbprint=$thumb;Installed=$false;
      Files=@(Get-ChildItem $package -File | ForEach-Object {[pscustomobject]@{Name=$_.Name;Hash=(Get-FileHash $_.FullName).Hash}})} |
      ConvertTo-Json -Depth 5 | Set-Content "$run/state.json" -Encoding UTF8
    Write-Host "Staged signed local-development candidate, NOT INSTALLED: $run"
  }
}finally{Pop-Location}
