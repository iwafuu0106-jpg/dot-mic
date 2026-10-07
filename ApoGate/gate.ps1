param([ValidateSet('restore','build','inspect','selftest','package')][string]$Action='build')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
  function Native([scriptblock]$command){& $command;if($LASTEXITCODE -ne 0){throw "Command failed: exit $LASTEXITCODE"}}
  function Restore-Headers {
    $lock=Get-Content ApoGate/headers.lock.json -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Force .deps/apo-win11-headers | Out-Null
    foreach($item in $lock.headers.PSObject.Properties){
      $path=".deps/apo-win11-headers/$($item.Name)"
      if(!(Test-Path $path)){Invoke-WebRequest "https://raw.githubusercontent.com/$($lock.repository)/$($lock.commit)/$($lock.basePath)/$($item.Name)" -OutFile $path}
      if((Get-FileHash $path).Hash.ToLowerInvariant() -ne $item.Value){throw "Pinned SDK header mismatch: $path"}
    }
  }
  function Get-GateCMake {
    $command=Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue
    if($command){return $command.Source}
    $path=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if(Test-Path $path){return $path};throw 'CMake 3.20+ required'
  }
  switch($Action){
    'restore' {Restore-Headers}
    'build' {
      Restore-Headers;$cmake=Get-GateCMake
      Native {& $cmake -S ApoGate -B artifacts/apo-gate/build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
      Native {& $cmake --build artifacts/apo-gate/build --config Release --parallel 2}
    }
    'inspect' {
      New-Item -ItemType Directory -Force artifacts/apo-gate | Out-Null
      $encoding=[Console]::OutputEncoding
      try {
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new()
        $text=& artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe inspect
      }finally{[Console]::OutputEncoding=$encoding}
      if($LASTEXITCODE -ne 0){throw 'Read-only endpoint probe failed'}
      $text -join "`n" | Set-Content artifacts/apo-gate/endpoint.json -Encoding UTF8
      Get-Content artifacts/apo-gate/endpoint.json -Raw
    }
    'selftest' {
      $dll=(Resolve-Path artifacts/apo-gate/build/Release/DotMic.ApoGate.dll).Path
      Native {& artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe selftest $dll}
    }
    'package' {
      # No elevation, certificate installation, OS changes, signing or driver installation here.
      New-Item -ItemType Directory -Force artifacts/apo-gate/package | Out-Null
      Copy-Item ApoGate/inf/*.inf artifacts/apo-gate/package -Force
      Copy-Item artifacts/apo-gate/build/Release/DotMic.ApoGate.dll artifacts/apo-gate/package -Force
      $kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
      Native {& "$kit/Tools/10.0.28000.0/x64/infverif.exe" /w artifacts/apo-gate/package/*.inf}
      Native {& "$kit/bin/10.0.28000.0/x86/Inf2Cat.exe" '/driver:artifacts/apo-gate/package' '/os:10_NI_X64,10_GE_X64' /uselocaltime}
      Get-FileHash artifacts/apo-gate/package/* | ForEach-Object {'{0}  {1}' -f $_.Hash.ToLowerInvariant(),(Split-Path $_.Path -Leaf)} | Set-Content artifacts/apo-gate/package.sha256 -Encoding UTF8
      Write-Host 'Gate package is UNSIGNED and NOT INSTALLED. INF/CAT construction does not prove audio-path feasibility.'
    }
  }
}finally{Pop-Location}
