# SQLite

Amalgamation SQLite 3.53.4 (domaine public, https://sqlite.org), compilée par
`../build-sqlite-windows.cmd` (Windows, MSVC) ou `../build-sqlite-linux.sh` (Linux, gcc).

Aucun binaire n'est versionné : la DLL est compilée à partir de ces sources par le workflow
GitHub Actions, ce qui permet de vérifier exactement ce qui est livré.
Options : FTS5 (recherche plein texte), threadsafe, pas de chargement d'extensions.
