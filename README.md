# PST Browser

Visualiseur **hors ligne** de fichiers **PST, OST et MSG**, conçu pour parcourir des exports eDiscovery
(Microsoft Purview) volumineux, sans Outlook et sans qu'aucune donnée ne quitte le poste.

- **Plusieurs PST ouverts ensemble** : boîtes différentes côte à côte, et parties d'une même boîte
  découpée (`dupont@x.fr.pst`, `dupont@x.fr_1.pst`…) fusionnées dans une seule arborescence.
- **Recherche globale** dans toutes les boîtes (ou une boîte, ou un dossier et ses sous-dossiers) :
  objet, expéditeur, destinataires, corps, noms des pièces jointes, contenu des messages joints.
  Casse et accents ignorés, réponse en quelques dizaines de millisecondes sur des centaines de milliers de messages.
- **Pensé pour des dizaines de Go** : index plein texte SQLite FTS5 construit une fois, en arrière-plan,
  avec reprise automatique après interruption ; les messages sont relus à la demande dans le PST.
- **Lecture sûre** : scripts, images distantes et liens bloqués ; aucune requête réseau.
- **Dédoublonnage** par Message-ID, **exports** CSV (liste des résultats), `.eml` (messages), pièces jointes.
- **Traçabilité** : fichiers sources ouverts en lecture seule, empreinte SHA-256 optionnelle.

## Utilisation

1. Téléchargez `PstBrowser.exe` dans les [Releases](../../releases) (exécutable unique, sans installation,
   sans droits administrateur ; Windows 10/11 x64). Vérifiez son empreinte avec `SHA256SUMS.txt`.
2. **Nouveau dossier d'affaire** : choisissez un dossier (idéalement sur un volume chiffré) qui contiendra l'index.
3. **Ajouter des fichiers / un dossier** (ou glisser-déposer) : PST, OST, MSG. L'indexation démarre aussitôt ;
   l'arborescence est consultable après quelques secondes, la recherche plein texte se complète au fil de l'eau.
4. Recherchez (Ctrl+F), parcourez les dossiers, ouvrez les messages et leurs pièces jointes.

### Syntaxe de recherche

| Saisie | Effet |
|---|---|
| `contrat signature` | tous les mots (ET implicite) |
| `"accord de confidentialité"` | expression exacte |
| `contra*` | préfixe |
| `devis OR facture` | l'un ou l'autre (`OU` accepté) |
| `-newsletter` | exclusion |
| `de:dupont` · `à:martin` · `objet:audit` · `corps:urgent` · `pj:xlsx` | restriction à un champ |
| `apres:2023-01-01 avant:2023-12-31` | période |

## Confidentialité et sécurité

- Aucune connexion réseau : pas de télémétrie, pas de mise à jour automatique, pas de service en ligne.
- Les PST/OST/MSG sont ouverts en **lecture seule** et ne sont jamais modifiés.
- L'index (`pstbrowser-index.db`) contient le texte des messages : il reste dans le dossier d'affaire choisi
  (placez-le sur un volume chiffré, supprimez-le en fin d'affaire).
- Affichage des messages dans WebView2 (moteur Edge) en mode InPrivate, JavaScript désactivé, toutes les requêtes
  interceptées : seul le contenu du message, servi depuis la mémoire, est affiché ; images distantes et pixels
  de suivi bloqués ; politique CSP stricte ; clic sur un lien = demande de confirmation.
- Les pièces jointes exécutables (`.exe`, `.js`, `.vbs`, `.lnk`, macros Office…) ne peuvent pas être ouvertes
  directement, seulement enregistrées. Les pièces jointes ouvertes sont copiées dans un dossier temporaire
  supprimé à la fermeture.
- Dépendances réduites au minimum et auditables (voir ci-dessous). SQLite est compilé depuis ses sources par la CI.

## Outil en ligne de commande

`pstbrowser-cli.exe` utilise le même moteur et le même format d'index (utile pour préparer un très gros
dossier d'affaire sur un serveur, ou pour des scripts) :

```
pstbrowser-cli add     D:\Affaire1 E:\Export\PST
pstbrowser-cli index   D:\Affaire1 --parallel 2 --sha256
pstbrowser-cli search  D:\Affaire1 "de:dupont pj:pdf" --csv resultats.csv
pstbrowser-cli eml     D:\Affaire1 12345 message.eml
```

## Architecture

```
src/PstBrowser.Core   moteur, multiplateforme (.NET 10)
  Mail/               lecture PST/OST (XstReader), MSG (lecteur Compound File maison), modèle commun
  Index/              dossier d'affaire, indexation en 2 passes (structure puis contenu), reprise
  Search/             syntaxe de recherche → FTS5, filtres, tri, export CSV
  Viewer/             rendu HTML sécurisé, RTF→HTML/texte, export .eml
  Data/               accès SQLite minimal (P/Invoke)
  Vendor/XstReader/   lecteur PST/OST (Ms-PL), modifié — voir les commentaires « PstBrowser modification »
src/PstBrowser.App    application Windows WPF + WebView2
src/PstBrowser.Cli    ligne de commande + tests de bout en bout (selftest)
native/sqlite         sources SQLite (amalgamation) et scripts de compilation
```

## Compiler

Prérequis : SDK .NET 10 ; pour la DLL SQLite sous Windows, les Build Tools Visual Studio (C++).

```
native\build-sqlite-windows.cmd
dotnet publish src\PstBrowser.App\PstBrowser.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
```

La CI (`.github/workflows/build.yml`) compile et teste à chaque push ; un tag `vX.Y.Z` déclenche
`.github/workflows/release.yml`, qui publie la release avec les empreintes SHA-256.

## Composants tiers

| Composant | Licence | Usage |
|---|---|---|
| [XstReader](https://github.com/iluvadev/XstReader) (Dijji, iluvadev) | Ms-PL (`src/PstBrowser.Core/Vendor/XstReader/LICENSE-Ms-PL.md`) | lecture des PST/OST |
| [SQLite](https://sqlite.org) | domaine public | index et recherche plein texte (FTS5) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | licence Microsoft (redistribuable) | affichage des messages |
| .NET / WPF | MIT | plateforme |

Fichiers de test (CI uniquement, non redistribués) : [pst-extractor](https://github.com/epfromer/pst-extractor),
[MSGReader](https://github.com/Sicos1977/MSGReader).
