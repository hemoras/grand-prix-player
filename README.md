# Grand Prix Player

Lecteur vidéo Windows léger pour les fichiers de Grand Prix à **plusieurs pistes vidéo et audio** :
jusqu'à 4 pistes vidéo affichées en même temps, synchronisées, et changement instantané de piste audio au clavier.

## Installer

Télécharger l'archive `GrandPrixPlayer-<version>-win-x64.zip` depuis la page
[Releases](https://github.com/hemoras/grand-prix-player/releases), la décompresser dans un dossier.
Elle contient `GrandPrixPlayer.exe` et `libmpv-2.dll`, qui doivent rester ensemble.
Il faut le runtime [.NET 8 Desktop](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows 64 bits).

- Double-clic sur `GrandPrixPlayer.exe`, puis **Ctrl+O** ou glisser un fichier sur la fenêtre ;
- ou « Ouvrir avec… » → `GrandPrixPlayer.exe` sur un `.mkv` / `.mp4`.

## Fonctionnement

À l'ouverture d'un fichier :
1. les **4 premières pistes vidéo** (moins s'il y en a moins) s'ouvrent en **mode déporté** : une fenêtre par vidéo ;
2. la **première piste audio** est jouée ;
3. si le fichier a plusieurs pistes audio, une fenêtre permet d'attribuer les numéros 1 à 9.
   Sélectionner une piste puis taper un chiffre : les deux pistes **échangent** leur numéro
   (1 = TF1, 2 = RTBF, 3 = BBC → sur BBC on tape 1 → BBC devient 1 et TF1 devient 3).
   Cette fenêtre se rouvre avec **Ctrl+T**. L'ordre peut être mémorisé par nom de piste
   pour les fichiers suivants (case à cocher). Dans ce cas, le n° 1 mémorisé devient la piste jouée par défaut ;
4. s'il y a plusieurs pistes vidéo, la fenêtre **« Choix des pistes audio principales »** permet de donner à chaque
   piste vidéo sa piste audio principale (la piste n° 1 par défaut). Ensuite, **un clic sur une vidéo** bascule sur
   sa piste audio principale. Les choix peuvent être mémorisés par nom de piste
   (ex. « Onboard » → « BBC ») pour les fichiers suivants. Cette fenêtre se rouvre avec **Ctrl+P**, et le choix
   d'une seule vidéo se modifie aussi par clic droit → *Piste audio principale (clic)*.

Clic sur une vidéo = clic simple (appui puis relâchement sans bouger). Si la souris bouge pendant l'appui,
c'est un glisser-déposer : en mode fenêtre unique, il échange deux vidéos.

Le **mode fenêtre unique** (Ctrl+D) regroupe les vidéos dans la fenêtre principale :
- disposition **grille** ou **principale + vignettes** (menu Vidéo ou clic droit) ;
- **glisser une vidéo sur une autre** pour les échanger ;
- **tirer les séparateurs** entre les vidéos pour les redimensionner.

En **plein écran** (fenêtre unique ou fenêtre déportée), amener la souris en bas de l'écran fait apparaître
une barre flottante : barre de défilement, lecture/pause, −10 s / +10 s, temps écoulé et piste audio en cours.
Elle disparaît moins d'une seconde après que la souris en est sortie.

En mode déporté, les fenêtres se déplacent et se redimensionnent librement (idéal en multi-écran).
Leurs positions sont mémorisées. La fenêtre principale sert alors de télécommande et affiche la liste des pistes audio (cliquables).

Clic droit sur une vidéo : choisir la piste vidéo affichée et sa piste audio principale, ajouter une vue (4 max.),
fermer la vue, plein écran.

## Raccourcis

| Touche | Action |
|---|---|
| Espace | Lecture / pause |
| ← / → | Reculer / avancer de 10 s (cumulable en appuis rapides) |
| ↑ / ↓ | Volume + / − |
| M | Muet |
| 1 … 9 (pavé numérique **ou** rangée du haut) | Piste audio n° 1 à 9 |
| Ctrl+T | Modifier les numéros des pistes audio |
| Ctrl+P | Pistes audio principales des vidéos |
| Clic sur une vidéo | Piste audio principale de cette vidéo |
| Ctrl+D | Basculer mode déporté / fenêtre unique |
| Ctrl+N | Ajouter une vue |
| F / F11, double-clic | Plein écran de la fenêtre active (souris en bas de l'écran : barre de défilement) |
| Échap | Quitter le plein écran |
| Ctrl+O | Ouvrir un fichier |
| F1 | Aide |

Les raccourcis fonctionnent depuis n'importe quelle fenêtre de l'application (fenêtres vidéo comprises).
Le pavé numérique doit être en mode Verr. Num.

## Architecture technique

- C# / WinForms (.NET 8) + **libmpv** (moteur du lecteur mpv, décodage ffmpeg, accélération matérielle).
- Un lecteur mpv « maître » **sans vidéo** joue la piste audio et sert d'horloge.
- Chaque vue est un lecteur mpv « esclave » **sans audio**, affichant une piste vidéo du même fichier.
  Toutes les 300 ms, l'écart avec le maître est mesuré. Une petite dérive est corrigée par une
  variation de vitesse imperceptible (±15 % max), un gros écart par un repositionnement.
  En test, l'écart reste sous 50 ms (environ une image).
- Pourquoi pas un fork de MPC-HC : son moteur DirectShow affiche une seule vidéo par instance. Afficher
  4 pistes simultanées aurait demandé de réécrire le cœur du rendu, avec une chaîne de compilation C++ lourde.

Fichiers : `Mpv.cs` (liaison libmpv), `MainForm.cs` (fenêtre principale, synchro, raccourcis),
`Controls.cs` (vues, grille, fenêtres déportées, barre de défilement, dialogue audio), `AppSettings.cs`.

Réglages : `%APPDATA%\GrandPrixPlayer\settings.json`. Journal d'erreurs : `%APPDATA%\GrandPrixPlayer\erreurs.log`.
Diagnostic de synchro : lancer avec la variable d'environnement `GPP_LOG=<chemin d'un fichier>`.

## Compiler

`libmpv-2.dll` (64 bits, ~116 Mo) n'est pas versionnée : elle dépasse la taille autorisée par GitHub.
Pour la récupérer à la racine du projet (7-Zip requis) :

```
powershell -ExecutionPolicy Bypass -File tools\get-libmpv.ps1
```

Elle provient de [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases)
(archive `mpv-dev-x86_64-*.7z`). Pour mettre à jour le moteur de lecture, il suffit de remplacer cette DLL.

```
dotnet publish -c Release -o publish
```

## Licences

Grand Prix Player s'appuie sur [mpv](https://github.com/mpv-player/mpv) via libmpv, distribuée sous licence
GPLv2+ (LGPL selon les options de compilation), et sur FFmpeg. Les sources de ces projets sont disponibles
sur leurs dépôts respectifs.
