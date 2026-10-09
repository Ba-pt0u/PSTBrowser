# PST Browser

Visualiseur **hors ligne** de fichiers **PST, OST et MSG**, conçu pour parcourir des exports eDiscovery
(Microsoft Purview) volumineux, sans Outlook et sans qu'aucune donnée ne quitte le poste.

- **Plusieurs PST ouverts ensemble** : boîtes différentes côte à côte, et parties d'une même boîte
  découpée (`dupont@x.fr.pst`, `dupont@x.fr_1.pst`…) fusionnées dans une seule arborescence.
- **Recherche globale** dans toutes les boîtes (ou une boîte, ou un dossier et ses sous-dossiers) :
  objet, expéditeur, destinataires, corps, noms des pièces jointes, contenu des messages joints.
  Casse et accents ignorés, réponse en quelques dizaines de millisecondes sur des centaines de milliers de messages.
- **Contenu des pièces jointes** (PDF, Word, Excel, PowerPoint, OpenDocument, archives ZIP, texte, e-mails…) indexé en
  option : un mot est trouvé s'il figure dans le message **ou** dans l'une de ses pièces jointes (recherche « famille »),
  avec l'empreinte SHA-256 de chaque pièce jointe. Pas d'OCR : les images scannées ne sont pas lues.
- **Liste des messages configurable** : colonnes à afficher (date de réception et d'envoi, adresses, À/Cc/Cci, chemin du
  dossier, fichier source, noms des pièces jointes, Message-ID, conversation, lu/non lu, suivi…), ordre, largeurs,
  tri sur toute colonne, vues « Standard » et « Investigation ».
- **Aide à l'enquête** : analyse des en-têtes (chemin des serveurs, SPF / DKIM / DMARC, indices d'usurpation),
  fils de conversation reconstitués entre toutes les boîtes, anomalies de dates, détection de données sensibles
  (IBAN, cartes bancaires, numéros de sécurité sociale, téléphones, valeurs masquées dans l'index).
- **Mise en forme conservée** pour les messages au format RTF (polices, tailles, couleurs, liens).
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
| `de:dupont` · `à:martin` · `objet:audit` · `corps:urgent` | restriction au message (expéditeur, destinataires, objet, corps) |
| `pj:xlsx` | noms des pièces jointes |
| `pjtexte:proaliance` | contenu des pièces jointes seulement (PDF, Office, texte…) |
| `apres:2023-01-01 avant:2023-12-31` | période |
| `indice:usurpation` · `replyto` · `returnpath` · `nomtrompeur` · `domaine` · `auth` | indices d'usurpation (voir ci-dessous) |
| `indice:dates` | dates incohérentes |
| `indice:sensible` · `iban` · `carte` · `secu` · `tel` | données sensibles détectées |
| `spf:fail` · `dkim:pass` · `dmarc:none` · `spf:absent` | résultat d'authentification (`pass`, `fail`, `softfail`, `neutral`, `none`, `temperror`, `permerror`, `absent`) |
| `fil:1234` | tous les messages d'un fil de conversation |

Un `-` devant un filtre l'inverse (`-indice:sensible`) ; les filtres se combinent avec les mots (`virement indice:usurpation`).

Sans champ, chaque mot doit se trouver dans le message **ou** dans l'une de ses pièces jointes (nom ou contenu) :
`budget signature` trouve un message dont le corps parle du budget et dont la pièce jointe contient « signature ».
Une exclusion (`-brouillon`) écarte le message si le mot figure dans le message ou dans l'une de ses pièces jointes.
Dans les résultats, un extrait « 📎 nom : extrait » indique la pièce jointe qui correspond ; dans le lecteur, les pièces
jointes qui contiennent les termes recherchés sont signalées (🔎) et « Aperçu du texte » affiche le texte extrait, surligné.

### Aide à l'enquête

Le bouton **Analyse** du lecteur (ou clic droit sur un message) ouvre, pour le message sélectionné :

- **En-têtes** : chemin des serveurs (`Received`, du plus ancien au plus récent, avec adresses IP et délais),
  résultats SPF, DKIM et DMARC, et les **indices d'usurpation** : adresse de réponse (`Reply-To`) ou `Return-Path` d'un autre
  domaine que l'expéditeur, nom affiché contenant l'adresse d'un autre domaine, nom d'un correspondant habituel utilisé avec
  une adresse d'un autre organisme, domaine ressemblant à un domaine bien plus fréquent du dossier (`paypa1.com`,
  `rnicrosoft.com`, même nom avec une autre extension, punycode).
- **Dates** : réception, envoi, création, modification, en-tête `Date:` et horodatages des serveurs rapprochés ; sont signalés
  un message reçu avant d'être envoyé, modifié avant d'être créé ou reçu, un en-tête très décalé, des serveurs datés à rebours,
  des dates futures ou antérieures à 1995 (tolérance d'horloge de 5 minutes).
- **Données sensibles** : IBAN (clé mod 97), cartes bancaires (clé de Luhn et préfixes d'émetteurs), numéros de sécurité sociale
  (clé de contrôle), téléphones (formats français et internationaux), dans le corps et dans les pièces jointes. L'index ne garde
  que le type, la position et une **valeur masquée** ; la valeur complète est relue à la demande dans le texte indexé.
- **Fil de conversation** : reconstitué avec `Message-ID`, `In-Reply-To`, `References` et l'index de conversation Outlook,
  les copies d'un même message dans plusieurs boîtes étant réunies ; à défaut, même objet, moins de 120 jours et un participant
  commun. La liste affiche un bandeau d'alerte et des colonnes dédiées (vue « Investigation »).

Ce sont des **indices à vérifier, pas des preuves** : listes de diffusion, services d'envoi en masse, décalages d'horloge,
outils de migration produisent aussi ces signes. Les index créés par une version précédente sont complétés automatiquement
(les en-têtes des messages déjà indexés sont relus une fois ; les fichiers ne sont pas réindexés).

### Contenu des pièces jointes

Après l'indexation des messages, une troisième étape (activée par défaut ; case à cocher dans **Sources…**) lit les pièces
jointes : PDF, Word (`.docx`, `.docm`, `.doc`), Excel (`.xlsx`, `.xlsm`, `.xls`), PowerPoint (`.pptx`, `.ppt`),
OpenDocument (`.odt`, `.ods`, `.odp`), archives ZIP (3 niveaux, avec protections contre les archives piégées),
`.txt`, `.csv`, `.ics`, `.vcf`, `.url`, `.log`, `.xml`, `.json`, `.html`, `.rtf`, `.eml`, `.msg`.
Chaque pièce jointe reçoit un statut (texte extrait, vide, non pris en charge, chiffré, trop gros — plus de 50 Mo —, erreur)
et une empreinte SHA-256. L'extraction s'exécute dans un **processus séparé** (`PstBrowser.exe --extract-worker`) avec un
délai maximal par fichier : un document corrompu ne peut pas bloquer ni faire planter l'application. L'étape est reprenable.
Les macros ne sont jamais exécutées ; aucun document n'est ouvert dans une application externe pour l'extraction.

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
pstbrowser-cli index   D:\Affaire1 --parallel 2 --sha256 [--no-attachments]
pstbrowser-cli search  D:\Affaire1 "de:dupont pjtexte:avenant" --sort sent:desc --csv resultats.csv
pstbrowser-cli attachments D:\Affaire1 --csv pieces-jointes.csv    (statut et SHA-256 de chaque pièce jointe)
pstbrowser-cli analyze D:\Affaire1 1234                              (en-têtes, dates, données sensibles, fil d'un message)
pstbrowser-cli sensitive D:\Affaire1 --csv sensibles.csv             (détections, valeurs masquées)
pstbrowser-cli lookalikes D:\Affaire1                                (domaines ressemblant à des domaines plus fréquents)
pstbrowser-cli eml     D:\Affaire1 12345 message.eml
```

## Architecture

```
src/PstBrowser.Core   moteur, multiplateforme (.NET 10)
  Mail/               lecture PST/OST (XstReader), MSG (lecteur Compound File maison), modèle commun
  Index/              dossier d'affaire (schéma 2, migration automatique), indexation en 3 passes (structure,
                      contenu, pièces jointes), reprise
  Analysis/           en-têtes (Received, SPF/DKIM/DMARC, usurpation), dates, données sensibles, domaines, fils
  Extraction/         texte des pièces jointes (PDF, Office, OpenDocument, zip…) et processus d'extraction séparé
  Search/             syntaxe de recherche structurée → FTS5 (recherche « famille »), filtres, tri, export CSV
  Viewer/             rendu HTML sécurisé, RTF→HTML/texte, export .eml
  Data/               accès SQLite minimal (P/Invoke)
  Vendor/XstReader/   lecteur PST/OST (Ms-PL), modifié — voir les commentaires « PstBrowser modification »
  Vendor/RtfPipe/     conversion RTF → HTML (MIT), modifié — images EMF/WMF écartées (System.Drawing)
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
| [RtfPipe](https://github.com/erdomke/RtfPipe) (Eric Domke) | MIT (`src/PstBrowser.Core/Vendor/RtfPipe/LICENSE-MIT.txt`) | mise en forme des messages RTF (sources intégrées, projet plus maintenu depuis 2021) |
| [PdfPig](https://github.com/UglyToad/PdfPig) (UglyToad) — paquet NuGet `PdfPig` 0.1.16 | Apache-2.0 | texte des PDF en pièces jointes |
| [SQLite](https://sqlite.org) | domaine public | index et recherche plein texte (FTS5) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | licence Microsoft (redistribuable) | affichage des messages |
| .NET / WPF | MIT | plateforme |

Fichiers de test (CI uniquement, non redistribués) : [pst-extractor](https://github.com/epfromer/pst-extractor),
[MSGReader](https://github.com/Sicos1977/MSGReader).
