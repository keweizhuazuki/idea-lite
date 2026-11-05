param([string]$Destination = 'D:\Codex\work\idea-lite-demo')
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw "Destination already exists: $destinationPath. Choose a new empty path." }
$sourcePath = Join-Path $PSScriptRoot '..\samples\spring-shop'
New-Item -ItemType Directory -Path $destinationPath | Out-Null
Copy-Item -LiteralPath (Join-Path $sourcePath 'pom.xml') -Destination $destinationPath
Copy-Item -LiteralPath (Join-Path $sourcePath 'README.md') -Destination $destinationPath
Copy-Item -LiteralPath (Join-Path $sourcePath 'src') -Destination $destinationPath -Recurse
Set-Content -LiteralPath (Join-Path $destinationPath '.gitignore') -Value "target/`n.idea/" -Encoding utf8
Push-Location -LiteralPath $destinationPath
try {
    git init -b main
    git config user.name 'Idea Lite Demo'
    git config user.email 'demo@example.invalid'
    git config core.autocrlf false
    git config commit.gpgsign false
    git add -- .
    git commit -m 'Initialize Spring shop sample'
    git switch -c feature/order-review
    $servicePath = Join-Path $destinationPath 'src\main\java\dev\idealite\shop\OrderService.java'
    $text = [IO.File]::ReadAllText($servicePath).Replace('399)', '349)')
    [IO.File]::WriteAllText($servicePath, $text, (New-Object Text.UTF8Encoding $false))
    git add -- .
    git commit -m 'Adjust keyboard price for the autumn sale'
    $text = $text.Replace('.orElseThrow();', '.orElseThrow(() -> new IllegalArgumentException("Order not found: " + id));')
    [IO.File]::WriteAllText($servicePath, $text, (New-Object Text.UTF8Encoding $false))
    $controllerPath = Join-Path $destinationPath 'src\main\java\dev\idealite\shop\OrderController.java'
    $controller = [IO.File]::ReadAllText($controllerPath).Replace('@GetMapping("/{id}")', '@GetMapping("/{id:[0-9]+}")')
    [IO.File]::WriteAllText($controllerPath, $controller, (New-Object Text.UTF8Encoding $false))
    Write-Output "Demo workspace ready: $destinationPath"
} finally { Pop-Location }
