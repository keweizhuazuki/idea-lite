param([string]$Destination = 'D:\Codex\work\idea-lite-benchmark', [ValidateRange(10,10000)][int]$Files = 2000)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw "Destination already exists: $destinationPath" }
New-Item -ItemType Directory -Path $destinationPath | Out-Null
$encoding = New-Object Text.UTF8Encoding $false
for ($i = 0; $i -lt $Files; $i++) {
    $packageNumber = [Math]::Floor($i / 20)
    $directoryPath = Join-Path $destinationPath "src\main\java\demo\p$packageNumber"
    [IO.Directory]::CreateDirectory($directoryPath) | Out-Null
    $source = @"
package demo.p$packageNumber;
import org.springframework.stereotype.Service;
@Service
public class Service$i {
    private final String name = "service-$i";
    public String getName() { return name; }
    public int calculate(int value) { return value * 2; }
    public boolean isReady() { return true; }
}
"@
    [IO.File]::WriteAllText((Join-Path $directoryPath "Service$i.java"), $source, $encoding)
}
[IO.File]::WriteAllText((Join-Path $destinationPath 'README.md'), "# Synthetic benchmark`n`n$Files generated Java files. No real project or dependencies.`n", $encoding)
Write-Output "Generated $Files Java files in $destinationPath"
