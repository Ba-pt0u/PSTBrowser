# Journal des modifications

Les notes de chaque release GitHub reprennent la section de la version correspondante.
Une version ne peut pas être publiée (`release.yml`) sans section datée dans ce fichier.

## [Non publié]

### Nouveautés — multiplateforme
- **Outil en ligne de commande pour macOS** (Apple Silicon et Intel, macOS 12 ou plus récent) **et Linux x64**
  (glibc 2.28 ou plus récente) : même moteur, même format d'index que sous Windows. Chaque version est testée sur
  Windows, Linux, macOS Apple Silicon et macOS Intel avant publication.
- **Attestations de provenance** (Sigstore) pour chaque fichier publié : `gh attestation verify <fichier> --repo Ba-pt0u/PSTBrowser`
  prouve qu'il a été compilé par GitHub Actions à partir de ce dépôt.

### Corrections
- Dossier d'affaire sur un partage réseau monté sous macOS ou Linux (SMB, NFS, AFP, sshfs…) : le journal SQLite
  adapté aux disques réseau est désormais utilisé, comme sous Windows.
- Pièces jointes exécutables propres à macOS et Linux (`.app`, `.command`, `.pkg`, `.dmg`, `.sh`, `.desktop`,
  `.AppImage`…) : jamais ouvertes directement, seulement enregistrables.

## [1.2.0] - 2026-10-19

### Nouveautés — aide à l'enquête
- **Analyse des en-têtes** (bouton **Analyse** du lecteur, clic droit sur un message) :
  - chemin des serveurs (`Received`, du plus ancien au plus récent, avec adresses IP et délais) ;
  - résultats **SPF, DKIM et DMARC** ;
  - **indices d'usurpation** : `Reply-To` ou `Return-Path` d'un autre domaine que l'expéditeur, nom affiché contenant l'adresse d'un autre domaine, nom d'un correspondant habituel utilisé avec une adresse d'un autre organisme, **domaines ressemblants** à des domaines plus fréquents du dossier (`paypa1.com`, `rnicrosoft.com`, même nom avec une autre extension, punycode).
- **Fils de conversation** reconstitués entre toutes les boîtes (`Message-ID`, `In-Reply-To`, `References`, index de conversation Outlook ; à défaut, même objet, moins de 120 jours et un participant commun) : bouton **Fil (n)** dans le lecteur, recherche `fil:N`.
- **Anomalies de dates** : message reçu avant d'être envoyé, modifié avant d'être créé ou reçu, en-tête `Date:` très décalé, serveurs datés à rebours, dates futures ou antérieures à 1995 (tolérance d'horloge de 5 minutes).
- **Données sensibles** : IBAN (clé mod 97), cartes bancaires (clé de Luhn et préfixes d'émetteurs), numéros de sécurité sociale (clé de contrôle), téléphones, dans le corps et les pièces jointes. L'index ne conserve que le type, la position et une **valeur masquée** ; la valeur complète est relue à la demande.
- Bandeau d'alerte dans le lecteur lorsqu'un indice est relevé.
- Nouveaux filtres de recherche : `indice:usurpation|replyto|returnpath|nomtrompeur|domaine|auth|dates|sensible|iban|carte|secu|tel`, `spf:`, `dkim:`, `dmarc:` (`pass`, `fail`, `softfail`, `none`, `absent`…), `fil:N` ; combinables et négatifs (`-indice:sensible`).
- Nouvelles colonnes de la liste (vue « Investigation ») : Fil, SPF / DKIM / DMARC, Usurpation ?, Dates, Sensible ; l'export CSV les reprend.
- Ligne de commande : `analyze`, `sensitive`, `lookalikes` ; options de diagnostic `analyze --raw` et `show --headers`.

### Mise à jour
- Le schéma de l'index passe à la version 3, **migration automatique** à l'ouverture d'un dossier d'affaire existant. Les messages ne sont pas réindexés : leurs en-têtes sont relus une fois dans les PST/MSG (phase « Analyse des en-têtes »), puis l'analyse transversale (fils, domaines, données sensibles) s'exécute à partir du texte déjà indexé. Prévoir ce passage de lecture sur un gros export ; la recherche reste utilisable pendant ce temps.
- Après la migration, les filtres `indice:` et `spf:` ne couvrent que les messages déjà analysés.

### À savoir
- Les indices sont des **éléments à vérifier, pas des preuves** : listes de diffusion, services d'envoi en masse, décalages d'horloge et outils de migration produisent aussi ces signes (par exemple, les invitations Google Agenda ont un `Return-Path` d'un autre domaine).
- Certains exports contiennent des en-têtes déjà tronqués ou altérés (cas du jeu de données Enron) : les dates et résultats d'authentification manquants ne peuvent alors pas être lus.
- Les numéros de téléphone sont reconnus par leur format, sans clé de contrôle : quelques faux positifs sont possibles.

## [1.1.0] - 2026-10-09

### Nouveautés
- **Contenu des pièces jointes indexé** (étape optionnelle, activée par défaut, case à cocher dans *Sources…*) : PDF, Word (`.docx`, `.docm`, `.doc`), Excel (`.xlsx`, `.xlsm`, `.xls`), PowerPoint (`.pptx`, `.ppt`), OpenDocument, archives ZIP (3 niveaux, protections contre les archives piégées), `.txt`, `.csv`, `.ics`, `.vcf`, `.url`, `.log`, `.xml`, `.json`, `.html`, `.rtf`, `.eml`, `.msg`.
  - L'extraction s'exécute dans un **processus séparé** avec un délai maximal par fichier : un document corrompu ne peut pas bloquer l'application.
  - Chaque pièce jointe reçoit un **statut** (texte extrait, vide, non pris en charge, chiffré, trop gros — au-delà de 50 Mo —, erreur) et une empreinte **SHA-256**.
  - Étape reprenable après interruption.
- **Recherche « famille »** : chaque mot doit se trouver dans le message **ou** dans l'une de ses pièces jointes ; `de:`, `à:`, `objet:`, `corps:` ne regardent que le message, `pj:` les noms de fichiers, nouveau `pjtexte:` le contenu des fichiers. Extraits « 📎 nom : extrait » dans les résultats.
- Lecteur : pièces jointes contenant les termes recherchés signalées (🔎), **Aperçu du texte** extrait avec termes surlignés.
- **Liste des messages configurable** : catalogue de colonnes (dates de réception et d'envoi, adresse de l'expéditeur, À/Cc/Cci, chemin du dossier, fichier source, noms des pièces jointes, Message-ID, conversation, lu/non lu, suivi…), affichage par clic droit sur un en-tête, ordre par glisser-déposer, largeurs mémorisées, vues **Standard** et **Investigation**, bouton Réinitialiser ; préférences propres à chaque utilisateur Windows.
- **Tri sur toute colonne** (en-tête de colonne ou liste déroulante).
- **Messages RTF** affichés avec leur mise en forme (polices, tailles, couleurs, liens) grâce à RtfPipe (MIT).
- Ligne de commande : `attachments` (statut et SHA-256 de chaque pièce jointe), `extract-file`, `--no-attachments`, `--sort champ[:asc|:desc]`.
- Export CSV enrichi (date d'envoi, Cc, Cci, Message-ID, conversation, lu/non lu, suivi, fichier source…).

### Mise à jour
- Le schéma de l'index passe à la version 2, **migration automatique** : les sources PST repassent par l'indexation du contenu et les sources MSG sont réindexées pour renseigner les nouvelles colonnes.

### Limites
- Pas d'OCR : les images scannées ne sont pas lues (les `.doc` qui ne contiennent qu'une image sont signalés « vide »).
- Les catégories Outlook ne sont pas disponibles (le lecteur PST ne sait pas lire la propriété nommée *Keywords*).
- Dans les messages RTF, les images EMF/WMF ne sont pas affichées.

### Composants ajoutés
- PdfPig 0.1.16 (Apache-2.0), RtfPipe (MIT, sources intégrées).

## [1.0.0] - 2026-10-08

Première version publique.
- Consultation hors ligne de fichiers **PST, OST et MSG** (exports eDiscovery Microsoft Purview), en lecture seule ; plusieurs boîtes ouvertes ensemble, parties d'une boîte découpée fusionnées.
- Index plein texte SQLite FTS5 construit en arrière-plan, reprenable ; recherche globale (objet, expéditeur, destinataires, corps, noms de pièces jointes, messages joints), casse et accents ignorés.
- Lecture sûre dans WebView2 (scripts, images distantes et liens bloqués), dédoublonnage par Message-ID, exports CSV, `.eml` et pièces jointes, empreinte SHA-256 optionnelle des sources.
- Outil en ligne de commande `pstbrowser-cli`.
