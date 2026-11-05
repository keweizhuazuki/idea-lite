param([string]$Destination = 'D:\Codex\work\idea-lite-demo')
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw "Destination already exists: $destinationPath. Choose a new path." }
$sourcePath = Join-Path $PSScriptRoot '..\samples\spring-shop'
New-Item -ItemType Directory -Path $destinationPath | Out-Null
foreach ($name in @('pom.xml','README.md','src')) {
    Copy-Item -LiteralPath (Join-Path $sourcePath $name) -Destination $destinationPath -Recurse
}
$utf8 = New-Object Text.UTF8Encoding $false
function Invoke-DemoGit {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "Demo Git command failed: $args" }
}
Push-Location -LiteralPath $destinationPath
try {
    [IO.File]::WriteAllText((Join-Path $destinationPath '.gitignore'), "target/$([char]10).idea/$([char]10)", $utf8)
    Invoke-DemoGit init -b main
    Invoke-DemoGit config user.name 'Idea Lite Demo'
    Invoke-DemoGit config user.email 'demo@example.invalid'
    Invoke-DemoGit config core.autocrlf false
    Invoke-DemoGit config commit.gpgsign false
    Invoke-DemoGit add -- .
    Invoke-DemoGit commit -m 'Initialize Spring shop'
    $javaPath = Join-Path $destinationPath 'src\main\java\dev\idealite\shop'
    $servicePath = Join-Path $javaPath 'OrderService.java'
    $controllerPath = Join-Path $javaPath 'OrderController.java'

    Invoke-DemoGit switch -c feature/order-lookup
    $controller = [IO.File]::ReadAllText($controllerPath).Replace('@GetMapping("/{id}")', '@GetMapping("/id/{id}")')
    [IO.File]::WriteAllText($controllerPath, $controller, $utf8)
    Invoke-DemoGit add -- .
    Invoke-DemoGit commit -m 'Expose the order lookup endpoint'

    Invoke-DemoGit switch main
    $service = [IO.File]::ReadAllText($servicePath).Replace('399)', '349)')
    [IO.File]::WriteAllText($servicePath, $service, $utf8)
    Invoke-DemoGit add -- .
    Invoke-DemoGit commit -m 'Adjust catalog prices'
    Invoke-DemoGit merge --no-ff feature/order-lookup -m 'Merge order lookup into the catalog'
    Invoke-DemoGit switch -c feature/order-review

    $service = $service.Replace('.orElseThrow();', '.orElseThrow(() -> new IllegalArgumentException("Order not found: " + id));')
    [IO.File]::WriteAllText($servicePath, $service, $utf8)
    Invoke-DemoGit add -- src/main/java/dev/idealite/shop/OrderService.java
    [IO.File]::WriteAllText($servicePath, $service.Replace('349)', '329)'), $utf8)
    [IO.File]::WriteAllText($controllerPath, $controller.Replace('/id/{id}', '/id/{id:[0-9]+}'), $utf8)
    Write-Output "Demo ready: $destinationPath"
} finally { Pop-Location }
