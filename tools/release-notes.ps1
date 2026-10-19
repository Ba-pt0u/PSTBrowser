# Builds the notes of a GitHub release: the CHANGELOG.md section of the version, then the downloads and SHA-256 fingerprints.
# Used by .github/workflows/release.yml (new release) and release-notes.yml (rewrite the notes of an existing release).
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Commit,
    [string] $SumsFile,
    [switch] $Attested,
    [string] $Changelog = (Join-Path $PSScriptRoot '..' 'CHANGELOG.md'),
    [string] $Output
)
$ErrorActionPreference = 'Stop'
$Version = $Version.Trim().TrimStart('v')
$lines = Get-Content -LiteralPath $Changelog -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match ('^## \[' + [regex]::Escape($Version) + '\]')) { $start = $i; break } }
if ($start -lt 0) { throw "CHANGELOG.md n'a pas de section « ## [$Version] » : rédigez les modifications de cette version avant de publier." }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## \[') { $end = $i; break } }
$body = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
if ($body.Length -lt 40) { throw "La section [$Version] de CHANGELOG.md est vide." }
# the sub-sections of the changelog (###) stay one level below the title of the notes
$sumLines = if ($SumsFile -and (Test-Path $SumsFile)) { @(Get-Content -LiteralPath $SumsFile -Encoding utf8 | Where-Object { $_.Trim() }) } else { @() }
$sums = if ($sumLines) { $sumLines -join "`n" } else { '(non disponibles)' }
$files = @($sumLines | ForEach-Object { ($_ -split '\s+', 2)[1] })
function Has([string] $name) { (-not $files) -and $name.EndsWith('.exe') -or $files -contains $name }

$downloads = [System.Collections.Generic.List[string]]::new()
$downloads.Add('**Windows 10 / 11 x64**')
if (Has 'PstBrowser.exe') { $downloads.Add("- ``PstBrowser.exe`` : l'application (exécutable unique, aucune installation, aucun droit administrateur). Elle affiche les messages avec le composant Microsoft Edge WebView2, fourni avec Windows.") }
if (Has 'pstbrowser-cli.exe') { $downloads.Add("- ``pstbrowser-cli.exe`` : le même moteur en ligne de commande (indexation de gros volumes, scripts).") }
$mac = @('pstbrowser-cli-macos-arm64.tar.gz', 'pstbrowser-cli-macos-x64.tar.gz') | Where-Object { $files -contains $_ }
if ($mac) {
    $downloads.Add('')
    $downloads.Add('**macOS 12 ou plus récent** — outil en ligne de commande')
    if ($files -contains 'pstbrowser-cli-macos-arm64.tar.gz') { $downloads.Add('- `pstbrowser-cli-macos-arm64.tar.gz` : Mac Apple Silicon (M1 et suivants).') }
    if ($files -contains 'pstbrowser-cli-macos-x64.tar.gz') { $downloads.Add('- `pstbrowser-cli-macos-x64.tar.gz` : Mac Intel.') }
    $downloads.Add("- L'exécutable n'est pas notarié par Apple : si macOS refuse de l'ouvrir, lever le blocage une fois avec ``xattr -d com.apple.quarantine pstbrowser-cli`` (après avoir vérifié l'empreinte).")
}
if ($files -contains 'pstbrowser-cli-linux-x64.tar.gz') {
    $downloads.Add('')
    $downloads.Add('**Linux x64** (glibc 2.28 ou plus récente : RHEL 8, Debian 12, Ubuntu 22.04 et suivantes) — outil en ligne de commande')
    $downloads.Add('- `pstbrowser-cli-linux-x64.tar.gz`')
}

$verify = [System.Collections.Generic.List[string]]::new()
$verify.Add('Vérification : `Get-FileHash .\PstBrowser.exe -Algorithm SHA256` (Windows)' + $(if ($mac -or $files -contains 'pstbrowser-cli-linux-x64.tar.gz') { ', `shasum -a 256 -c SHA256SUMS.txt --ignore-missing` (macOS, Linux).' } else { '.' }))
if ($Attested) {
    $verify.Add('')
    $verify.Add("Chaque fichier porte une attestation de provenance signée (Sigstore) qui prouve qu'il a été compilé par GitHub Actions à partir de ce dépôt : ``gh attestation verify <fichier> --repo Ba-pt0u/PSTBrowser` (GitHub CLI)`.")
}

$notes = @"
## PST Browser $Version

$body

---

### Téléchargements
$($downloads -join "`n")

Empreintes SHA-256 :
``````
$sums
``````
$($verify -join "`n")

Compilé par GitHub Actions à partir du commit $Commit. Historique complet : [CHANGELOG.md](https://github.com/Ba-pt0u/PSTBrowser/blob/main/CHANGELOG.md).
"@
if ($Output) { $notes | Set-Content -LiteralPath $Output -Encoding utf8 } else { $notes }
