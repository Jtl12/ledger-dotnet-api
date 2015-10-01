del *.nupkg
C:\Windows\Microsoft.NET\Framework\v4.0.30319\msbuild.exe "..\BTChip\BTChip.csproj" -p:Configuration=Release

nuGet pack

forfiles /m *.nupkg /c "cmd /c NuGet.exe push @FILE"
(((dir *.nupkg).Name) -match "[0-9]+?\.[0-9]+?\.[0-9]+?\.[0-9]+")
$ver = $Matches.Item(0)
git tag -a "v$ver" -m "$ver"
git push --tags