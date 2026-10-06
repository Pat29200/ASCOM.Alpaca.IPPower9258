# ASCOM Alpaca IP Power 9258 — v0.14

Pilote Windows ASCOM Alpaca Switch pour les prises réseau Aviosys IP Power
9258/9258xx. Il permet de commander les quatre sorties depuis N.I.N.A.
et les autres clients compatibles Alpaca.

Un script facultatif permet de programmer une coupure différée. Son utilisation
est à configurer selon les besoins ; installer le pilote ne programme aucune
coupure et n'active aucune séquence d'arrêt du PC.

Auteur : **Pat29200**. Co-auteur IA : **ChatGPT**.
Licence du code du projet : [MIT](LICENSE). Les dépendances gardent leurs licences.

## Matériel et fonctionnement

Validation matérielle effectuée sur un IP Power 9258 avec firmware V4.01
(2013/04/18). La compatibilité avec d'autres firmwares reste à vérifier.
Le pilote utilise Telnet (port 23 par défaut). Le BAT utilise HTTP Basic sur
le port 80. Le serveur Alpaca écoute sur le port 5000 par défaut ; la découverte
utilise UDP 32227.

Les quatre sorties sont affichées avec leur état réel dans NINA. Les noms des
prises se configurent dans la page de configuration ; privilégier des noms
sans accents pour les clients qui les affichent mal.

## Installation

Télécharger l'installateur EXE depuis la rubrique Releases du dépôt lorsqu'il
sera disponible. Lancer l'installation avec les droits administrateur et indiquer
le compte Windows qui exécute NINA, par exemple `MON-PC\telescope`.

L'installateur installe le serveur dans `C:\Program Files\ASCOM-IPPower9258`,
crée une tâche de démarrage sous SYSTEM et une règle entrante Windows Defender
pour les réseaux privés. Il installe aussi le BAT dans
`C:\ASCOM-IPPower9258-Scripts` sur le lecteur système.

Ouvrir <http://127.0.0.1:5000/setup>, renseigner l'adresse de la prise et ses
identifiants, tester la connexion puis enregistrer. Si le port Alpaca est
modifié, redémarrer le serveur et utiliser ce nouveau port dans l'URL ; les
raccourcis de l'installateur restent configurés sur 5000.

La configuration est stockée sous `%ProgramData%\ASCOM-IPPower9258`.
Le mot de passe est protégé par DPAPI pour cette machine. Le compte NINA reçoit
la lecture de la configuration et peut déchiffrer ce mot de passe.
La désinstallation conserve la configuration pour une réinstallation.

Utiliser le serveur sur un réseau local de confiance. Telnet et HTTP Basic ne
chiffrent pas les échanges avec la prise. Les réglages de VPN et les pare-feu
supplémentaires peuvent empêcher les connexions locales.

## Option : script de coupure différée

Cette option est indépendante du contrôle des prises. Pour utiliser uniquement
le pilote, il suffit de ne pas ajouter le BAT à une séquence NINA et de ne pas
lancer le script. Le BAT est fourni comme exemple pour une affectation précise
des sorties : prise 2 pour le PC, prise 4 libre pour le test. Leurs numéros et
les délais sont définis dans le script ; ils ne se règlent pas dans la page
de configuration du pilote.

Le BAT prend l'adresse et les identifiants dans la configuration du pilote.
Vérifier cette adresse et l'affectation physique des sorties avant utilisation.
Les numéros ci-dessous désignent les sorties physiques, numérotées de 1 à 4.

| Argument | Action |
| --- | --- |
| `/check` | Lit les quatre états, sans modification. |
| `/test` | Allume la prise 4 puis programme son extinction après 30 secondes. La prise 4 doit être sans appareil branché. |
| Aucun | Allume la prise 2 puis programme son extinction après 600 secondes. |

```powershell
& "C:\ASCOM-IPPower9258-Scripts\IPPower9258-Arret-PC-600s.bat" /check
```

Le compte à rebours est exécuté par l'IP9258. Le BAT ne lance pas l'arrêt de
Windows. Dans NINA, placer son exécution sans argument avant `Shutdown PC`.
600 secondes est un délai choisi pour l'installation de référence, pas une
garantie pour toutes les mises à jour Windows. La prise 2 doit alimenter le PC
que l'on souhaite arrêter.

Le script retourne 0 lorsqu'une réponse attendue est reconnue, 1 en cas d'échec
et 2 pour un argument non reconnu. Une confirmation indique la réception de
la commande ; contrôler aussi l'extinction réelle lors du test initial.

Le firmware peut laisser la connexion HTTP ouverte après avoir envoyé l'état.
Le BAT lit la réponse progressivement et reconnaît les états sans attendre la
fermeture TCP, avec un délai de lecture limité.

## Compilation du pilote

Prérequis : SDK .NET 10 sur Windows. Depuis la racine du dépôt :

```powershell
dotnet publish .\ASCOM.Alpaca.IPPower9258.csproj -c Release -r win-x64 --self-contained true -o .\bin\Release\net10.0\publish
```

La publication contient le runtime ; le PC destinataire n'a pas besoin
d'installer séparément .NET pour cette publication autonome.

## Compilation de l'installateur

Après publication du pilote, ouvrir
`installer\ASCOM-IPPower9258-v0.14.iss` dans Inno Setup et compiler.
La source utilise le dossier de publication relatif au dépôt.
L'EXE est produit dans `artifacts` et sera joint à une Release GitHub.
Les fichiers générés et les réglages personnels ne sont pas inclus dans les sources.

## Validation et limites de ce paquet

La v0.14 fournie a été testée en mise à jour et en première installation, avec
connexion, sauvegarde des paramètres, démarrage automatique après redémarrage,
désinstallation/réinstallation et test de coupure de la prise 4 après 30 secondes.
Le BAT révision 4 a été validé sur le PC de télescope et un troisième PC.

Ce paquet public conserve le code du pilote. Il adapte les chemins de compilation
de l'installateur et remplace l'adresse fixe du BAT par l'adresse configurée.
Le 6 octobre 2026, la publication .NET et la compilation de l'installateur
ont réussi sur Windows. L'EXE correspondant a ensuite été installé sur le PC
de télescope : `/check`, `/test` (prise 4 allumée puis éteinte après 30 secondes),
connexion du pilote et démarrage du serveur après remise sous tension ont
été validés.

Le démarrage automatique de NINA relève d'une tâche Windows distincte, à
configurer séparément ; cet installateur ne crée ni ne modifie cette tâche.

L'application Android est un projet séparé et n'est pas incluse ici.
