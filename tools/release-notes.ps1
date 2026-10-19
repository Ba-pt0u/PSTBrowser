# Builds the notes of a GitHub release: the CHANGELOG.md section of the version, then the downloads and SHA-256 fingerprints.
# Used by .github/workflows/release.yml (new release) and release-notes.yml (rewrite the notes of an existing release).
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Commit,
    [string] $SumsFile,
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
$sums = if ($SumsFile -and (Test-Path $SumsFile)) { (Get-Content -LiteralPath $SumsFile -Encoding utf8 | Where-Object { $_.Trim() }) -join "`n" } else { '(non disponibles)' }
$notes = @"
## PST Browser $Version

$body

---

### Téléchargements
- ``PstBrowser.exe`` : l'application (exécutable unique, aucune installation, aucun droit administrateur).
- ``pstbrowser-cli.exe`` : le même moteur en ligne de commande (indexation de gros volumes, scripts).

Prérequis : Windows 10 ou 11 x64 (le composant Microsoft Edge WebView2, fourni avec Windows, est utilisé pour afficher les messages).

Empreintes SHA-256 :
``````
$sums
``````
Vérification : ``Get-FileHash .\PstBrowser.exe -Algorithm SHA256``

Compilé par GitHub Actions à partir du commit $Commit. Historique complet : [CHANGELOG.md](https://github.com/Ba-pt0u/PSTBrowser/blob/main/CHANGELOG.md).
"@
if ($Output) { $notes | Set-Content -LiteralPath $Output -Encoding utf8 } else { $notes }
