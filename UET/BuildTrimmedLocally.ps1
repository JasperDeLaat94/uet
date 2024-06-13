$Timestamp = ([DateTime]::UtcNow)
$PackageVersion = "$($Timestamp.Year).$($Timestamp.DayOfYear + 1000).$(($Timestamp.Hour * 60) + $Timestamp.Minute)"

Push-Location $PSScriptRoot\..
try {
  dotnet msbuild -restore -p:RuntimeIdentifier=win-x86 -p:Configuration=Release -p:Platform=x86 -p:BaseUetVersionSuffix="-dev" -p:BaseUetVersion=$PackageVersion -p:PackageVersion=$PackageVersion UET/Redpoint.AutoDiscovery.Win32/Redpoint.AutoDiscovery.Win32.csproj
  if ($LastExitCode -ne 0) {
    exit $LastExitCode 
  }
  dotnet msbuild -restore -p:RuntimeIdentifier=win-x64 -p:Configuration=Release -p:Platform=x64 -p:BaseUetVersionSuffix="-dev" -p:BaseUetVersion=$PackageVersion -p:PackageVersion=$PackageVersion UET/Redpoint.AutoDiscovery.Win64/Redpoint.AutoDiscovery.Win64.csproj
  if ($LastExitCode -ne 0) {
    exit $LastExitCode 
  }
  dotnet msbuild -restore -t:Publish -p:Configuration=Release -p:Platform=AnyCPU -p:UsePrebuiltLibsForAutoDiscovery=true -p:BaseUetVersionSuffix="-dev" -p:BaseUetVersion=$PackageVersion -p:PackageVersion=$PackageVersion UET/Redpoint.AutoDiscovery/Redpoint.AutoDiscovery.csproj
  if ($LastExitCode -ne 0) {
    exit $LastExitCode 
  }
  dotnet msbuild -restore -t:Publish -p:RuntimeIdentifier=win-x64 -p:UsePrebuiltLibsForAutoDiscovery=true -p:Configuration=Release -p:BaseUetVersionSuffix="-dev" -p:BaseUetVersion=$PackageVersion -p:PackageVersion=$PackageVersion UET/uet/uet.csproj
  if ($LastExitCode -ne 0) {
    exit $LastExitCode 
  }
  dotnet msbuild -restore -t:Publish -p:RuntimeIdentifier=win-x64 -p:UsePrebuiltLibsForAutoDiscovery=true -p:Configuration=Release -p:EmbeddingCrossPlatform=true -p:BaseUetVersionSuffix="-dev" -p:BaseUetVersion=$PackageVersion -p:PackageVersion=$PackageVersion UET/uet/uet.csproj
  if ($LastExitCode -ne 0) {
    exit $LastExitCode 
  }
} finally {
  Pop-Location
}
