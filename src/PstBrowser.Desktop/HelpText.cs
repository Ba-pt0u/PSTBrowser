namespace PstBrowser.Desktop
{
    /// <summary>Text of the help window.</summary>
    internal static class HelpText
    {
        public const string Text =
@"PST BROWSER — AIDE

DOSSIER D'AFFAIRE
  Un dossier d'affaire contient l'index de recherche (pstbrowser-index.db) d'un ensemble de fichiers.
  Les fichiers PST, OST et MSG restent à leur emplacement, ouverts en lecture seule, jamais modifiés.
  L'index contient le texte des messages : placez le dossier d'affaire sur un support protégé
  (volume chiffré FileVault ou LUKS par exemple) et supprimez-le en fin d'affaire.

AJOUTER DES SOURCES
  « Ajouter des fichiers » : PST, OST ou MSG. « Ajouter un dossier » : tous les PST/OST du dossier
  et de ses sous-dossiers, et tous les MSG (l'arborescence des MSG est conservée). Glisser-déposer possible.
  Les parties d'une boîte découpée (ex. dupont@x.fr.pst, dupont@x.fr_1.pst) sont regroupées automatiquement
  dans une même boîte ; le regroupement se corrige dans « Sources… ».

RECHERCHE (⌘F sur Mac, Ctrl+F sous Linux)
  mots simples          tous les mots doivent être présents (casse et accents ignorés)
  ""expression exacte""   expression exacte
  contra*               mots commençant par « contra »
  devis OR facture      l'un ou l'autre (OU fonctionne aussi)
  -newsletter           exclut les messages contenant ce mot
  de:dupont             expéditeur (nom ou adresse)
  à:martin              destinataires (À, Cc, Cci)
  objet:contrat         objet
  corps:confidentiel    corps du message
  pj:pdf                nom des pièces jointes
  pjtexte:avenant       contenu des pièces jointes uniquement (PDF, Word, Excel, PowerPoint, texte…)
  apres:2023-01-01  avant:2023-12-31   période (ou utilisez les champs « du / au »)

  La recherche porte sur l'objet, l'expéditeur, les destinataires, le corps, les noms et le contenu des pièces jointes
  et le contenu des messages joints (mails transférés en pièce jointe).
  Sans champ, chaque mot doit se trouver dans le message OU dans l'une de ses pièces jointes :
  « budget signature » trouve un message qui parle du budget dont la pièce jointe contient « signature ».
  de: à: objet: corps: ne regardent que le message ; pj: que les noms de fichiers ; pjtexte: que le contenu des fichiers.
  Un -mot exclut le message si le mot figure dans le message ou dans l'une de ses pièces jointes.
  Dans la liste, un extrait « 📎 nom : extrait » indique la pièce jointe qui correspond à la recherche.
  Portée : partout (par défaut), boîte sélectionnée, ou dossier sélectionné et ses sous-dossiers.
  « Sans doublons » masque les copies d'un même message (même Message-ID) dans une même boîte.

CONTENU DES PIÈCES JOINTES
  Après les messages, une étape supplémentaire lit les pièces jointes (PDF, Word, Excel, PowerPoint, OpenDocument,
  ZIP, texte, e-mails…) dans un processus séparé et calcule leur empreinte SHA-256. Case à cocher dans « Sources… ».
  Les fichiers de plus de 50 Mo, chiffrés ou d'un format non pris en charge sont signalés mais non lus ;
  les images scannées ne sont pas reconnues (pas d'OCR). L'étape est reprenable après une interruption.
  Dans le lecteur, les pièces jointes qui contiennent les termes recherchés sont signalées (🔎) ;
  clic sur une pièce jointe puis « Aperçu du texte » : texte extrait, termes surlignés.

AIDE À L'ENQUÊTE
  Bouton « Analyse » du lecteur (ou clic droit sur un message) : chemin des serveurs, SPF / DKIM / DMARC, indices
  d'usurpation (Reply-To ou Return-Path d'un autre domaine, nom affiché trompeur, domaine ressemblant), dates
  incohérentes, données sensibles (IBAN, cartes, sécurité sociale, téléphones : valeurs masquées) et fil de conversation.
  Recherche : indice:usurpation  indice:replyto  indice:returnpath  indice:nomtrompeur  indice:domaine  indice:auth
              indice:dates  indice:sensible  indice:iban  indice:carte  indice:secu  indice:tel
              spf:fail  dkim:pass  dmarc:none  spf:absent    fil:1234 (un fil de conversation)
  Ce sont des indices à vérifier, pas des preuves (listes de diffusion, services d'envoi, horloges, migrations…).
  Le bouton « Fil (n) » du lecteur liste tous les messages de la conversation, toutes boîtes confondues.

COLONNES DE LA LISTE
  Bouton « Colonnes… » : afficher ou masquer des colonnes, changer leur ordre, vues « Standard »
  et « Investigation », « Réinitialiser ». Glisser un en-tête pour changer l'ordre, tirer son bord pour la largeur.
  Un clic sur un en-tête trie la liste (second clic : sens inverse) ; la liste déroulante permet aussi de choisir le tri.
  Les préférences sont enregistrées pour chaque utilisateur du poste.

LECTURE
  Les images distantes, scripts et liens sont bloqués : aucune connexion réseau n'est établie.
  Les messages au format RTF sont affichés avec leur mise en forme (polices, couleurs, liens).
  Un clic sur un lien demande confirmation avant de l'ouvrir dans votre navigateur.
  Double-clic ou Entrée : ouvrir le message dans une fenêtre. Les messages joints s'ouvrent d'un clic.
  Les pièces jointes exécutables (.exe, .app, .command, .pkg, .sh, .desktop, .AppImage…) ne s'ouvrent jamais
  directement : enregistrez-les puis faites-les analyser.

EXPORTS
  Liste des résultats en CSV (Excel), messages sélectionnés en .eml, pièces jointes une par une ou toutes.";
    }
}
