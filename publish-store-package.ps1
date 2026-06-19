<#
.SYNOPSIS
    Gera o pacote .appxupload do ClefExplorer para upload manual na Microsoft Store.

.DESCRIPTION
    Automatiza a geração do pacote de submissão (.appxupload):
      1. Carimba a versão informada no Package.appxmanifest.
      2. (Opcional) Faz build LIMPO (apaga bin/obj) — evita reaproveitar um .appx de
         aplicação "stale" com versão antiga, que a Store recusa por nome duplicado.
      3. Empacota em modo StoreUpload, SEM assinar (a Store re-assina). O
         Package.StoreAssociation.xml local troca o publisher para o da Store.
      4. VERIFICA que a versão DENTRO do bundle (app + recursos) bate com a pedida.
      5. Mostra o caminho do .appxupload e abre a pasta.

    Por padrão o Package.appxmanifest é restaurado ao final (a versão fica só no pacote).

    NÃO usa GenerateTestArtifacts=false nem AppxPackageDir: ambos suprimem os
    pacotes de recurso de escala (scale-100/125/150/400) e geram bundle incompleto.

.PARAMETER Version
    Versão do pacote (ex.: 1.2.0 ou 1.2.0.0; a 4ª parte é forçada para 0).
    DEVE ser MAIOR que a versão atualmente publicada na Store.

.PARAMETER KeepManifestVersion
    Mantém a versão carimbada no Package.appxmanifest (por padrão ele é restaurado).

.PARAMETER NoClean
    Pula a limpeza de bin/obj (mais rápido, porém arrisca versão interna "stale").

.PARAMETER Platform
    Plataforma do bundle. Padrão: x64.

.EXAMPLE
    .\publish-store-package.ps1 -Version 1.2.0

.EXAMPLE
    .\publish-store-package.ps1 -Version 1.2.0 -KeepManifestVersion
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Platform = 'x64',
    [string]$Configuration = 'Release',
    [switch]$KeepManifestVersion,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

# ---------- Caminhos ----------
$root        = $PSScriptRoot
$srcProj     = Join-Path $root 'src\ClefExplorer.csproj'
$pkgProj     = Join-Path $root 'ClefExplorer.Package\ClefExplorer.Package.wapproj'
$manifest    = Join-Path $root 'ClefExplorer.Package\Package.appxmanifest'
$appPackages = Join-Path $root 'ClefExplorer.Package\AppPackages'

foreach ($p in @($srcProj, $pkgProj, $manifest)) {
    if (-not (Test-Path $p)) { throw "Não encontrei: $p (rode o script da raiz do repositório)." }
}

# ---------- Normaliza a versão para x.y.z.0 ----------
$v = $Version.Trim().TrimStart('v', 'V')
if ($v -notmatch '^\d+\.\d+(\.\d+){0,2}$') {
    throw "Versão inválida: '$Version'. Use algo como 1.2.0 ou 1.2.0.0."
}
$parts = @($v.Split('.'))
# A Store reserva a 4ª parte (revisão) — só aceita 0. Se vier 4 partes, a revisão deve ser 0.
if ($parts.Count -eq 4 -and $parts[3] -ne '0') {
    throw "A 4ª parte da versão (revisão) deve ser 0 — a Store a reserva. Recebido: '$Version'."
}
while ($parts.Count -lt 3) { $parts += '0' }
$fullVersion = '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]
Write-Host "Versão do pacote: $fullVersion" -ForegroundColor Cyan

# ---------- Dica: versão já instalada/publicada ----------
try {
    $installed = Get-AppxPackage -Name 'AndersonFernandes.ClefExplorer' -ErrorAction SilentlyContinue
    if ($installed) {
        $maxInstalled = ($installed | ForEach-Object { [version]$_.Version } | Sort-Object -Descending | Select-Object -First 1)
        Write-Host "Versão mais alta instalada/publicada localmente: $maxInstalled" -ForegroundColor DarkGray
        if ([version]$fullVersion -le $maxInstalled) {
            Write-Warning "A versão $fullVersion NÃO é maior que $maxInstalled — a Store provavelmente recusará. Use uma maior."
        }
    }
} catch { }

# ---------- Localiza o MSBuild (VS) ----------
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild) { $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source }
if (-not $msbuild) { throw "MSBuild não encontrado. Abra um 'Developer PowerShell for VS' ou instale o Visual Studio." }
Write-Host "MSBuild: $msbuild" -ForegroundColor DarkGray

# ---------- Verifica a versão interna do bundle ----------
function Test-BundleVersions {
    param([string]$AppxUpload, [string]$Expected)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("clef_verify_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        Copy-Item $AppxUpload (Join-Path $tmp 'u.zip')
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $tmp 'u.zip'), (Join-Path $tmp 'u'))
        $bundle = Get-ChildItem (Join-Path $tmp 'u') -Filter *.appxbundle | Select-Object -First 1
        if (-not $bundle) { throw "Bundle .appxbundle não encontrado dentro do .appxupload." }
        Copy-Item $bundle.FullName (Join-Path $tmp 'b.zip')
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $tmp 'b.zip'), (Join-Path $tmp 'b'))
        [xml]$bm = Get-Content (Join-Path $tmp 'b\AppxMetadata\AppxBundleManifest.xml')
        $pkgs = @($bm.Bundle.Packages.Package)
        $bad  = $pkgs | Where-Object { $_.Version -ne $Expected }
        Write-Host "Identidade do bundle: $($bm.Bundle.Identity.Name) $($bm.Bundle.Identity.Version)" -ForegroundColor DarkGray
        foreach ($p in $pkgs) { Write-Host ("  - {0,-11} {1} {2}" -f $p.Type, $p.Version, $p.Architecture) -ForegroundColor DarkGray }
        if (-not ($pkgs | Where-Object { $_.Type -eq 'application' })) { throw "O bundle não contém pacote de aplicação." }
        if ($bad) { throw "Versão interna divergente: esperado $Expected, mas há pacotes em $((($bad.Version) | Sort-Object -Unique) -join ', '). Rode SEM -NoClean." }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------- Pipeline ----------
$originalManifest = Get-Content -Raw -Path $manifest
try {
    # Encerra instâncias que possam travar arquivos
    Get-Process ClefExplorer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    # Carimba a versão (regex preserva a formatação do XML)
    $patched = [regex]::Replace($originalManifest, '(<Identity\b[^>]*?\bVersion=")[^"]*(")', "`${1}$fullVersion`${2}")
    if ($patched -eq $originalManifest) { throw "Não consegui localizar/atualizar a <Identity Version> no manifesto." }
    Set-Content -Path $manifest -Value $patched -Encoding UTF8 -NoNewline

    # Build limpo
    if (-not $NoClean) {
        Write-Host "Limpando bin/obj/AppPackages..." -ForegroundColor Cyan
        foreach ($d in @(
            (Join-Path $root 'src\bin'), (Join-Path $root 'src\obj'),
            (Join-Path $root 'ClefExplorer.Package\bin'), (Join-Path $root 'ClefExplorer.Package\obj'),
            $appPackages, (Join-Path $root 'ClefExplorer.Package\BundleArtifacts')
        )) {
            if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    # Empacota (StoreUpload, sem assinar). NÃO adicionar GenerateTestArtifacts/AppxPackageDir.
    Write-Host "Compilando e empacotando ($Configuration|$Platform)..." -ForegroundColor Cyan
    & $msbuild $pkgProj `
        /restore `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:AppxBundle=Always `
        /p:AppxBundlePlatforms=$Platform `
        /p:UapAppxPackageBuildMode=StoreUpload `
        /p:AppxPackageSigningEnabled=false `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "MSBuild falhou (exit $LASTEXITCODE)." }
}
finally {
    if (-not $KeepManifestVersion) {
        Set-Content -Path $manifest -Value $originalManifest -Encoding UTF8 -NoNewline
        Write-Host "Manifesto restaurado para a versão original." -ForegroundColor DarkGray
    } else {
        Write-Host "Manifesto mantido com a versão $fullVersion (-KeepManifestVersion)." -ForegroundColor DarkGray
    }
}

# ---------- Localiza e valida o pacote ----------
$appxupload = Get-ChildItem $appPackages -Recurse -Include '*.appxupload', '*.msixupload' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appxupload) { throw "Pacote .appxupload não foi gerado em $appPackages." }

Write-Host ""
Write-Host "Validando versão interna do pacote..." -ForegroundColor Cyan
Test-BundleVersions -AppxUpload $appxupload.FullName -Expected $fullVersion

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Green
Write-Host " PACOTE PRONTO PARA UPLOAD MANUAL" -ForegroundColor Green
Write-Host "   $($appxupload.FullName)" -ForegroundColor Green
Write-Host "   Tamanho: {0:N1} MB" -f ($appxupload.Length / 1MB) -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Suba este .appxupload em: Partner Center > Clef Explorer > novo envio > Pacotes." -ForegroundColor Yellow

# Abre a pasta com o arquivo selecionado
Start-Process explorer.exe "/select,`"$($appxupload.FullName)`""
