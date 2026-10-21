# MortisPaint

Application de dessin fait main, inspirée d'ibisPaint (fonctions « premium » comprises), écrite en C# avec **Avalonia 12** et **SkiaSharp 3**. Une seule base de code pour **Windows (PC)** et **Android**, avec une interface responsive :

- **Grand écran** (≥ 760 px) : rail d'outils à gauche, panneau Pinceau / Couleur / Calques à droite.
- **Téléphone** : barre d'outils en bas, panneaux en tiroir, curseurs taille / opacité sur le côté (comme ibisPaint).

## Style

Même famille que MortisClouds et Memento : thème sombre monochrome (palette de nekuzaky.com), action principale blanche, filets gris translucides, titres en Georgia, rayons 8 / 12 px ; ce qui flotte (menus, panneaux, dialogues) est éclairé sur son bord haut et porte une grande ombre douce. La couleur d'accent se change dans Préférences → Couleur d'accent (monochrome par défaut, orange Memento, ruban, bleu…). Le rouge « ruban » reste réservé à ce que l'app fait d'elle-même : la pastille REC du timelapse. Icône : le livre Mortis au trait, une plume sur la couverture, le ruban en marque-page.

## Compatibilité ibisPaint

ibisPaint importe et exporte le **PSD à calques** (« PSD (calques conservés) »). MortisPaint lit et écrit ce format :

- **ibisPaint → MortisPaint** : dans ibisPaint, exporter l'œuvre en PSD (calques conservés), puis dans MortisPaint *Ouvrir* le fichier depuis le carnet.
- **MortisPaint → ibisPaint** : menu ☰ → *Exporter en PSD* (ou depuis le carnet), puis importer le PSD dans ibisPaint.

Conservés : calques, **dossiers imbriqués** (nom, visibilité, opacité, mode, ouvert/fermé), noms (Unicode), visibilité, opacité, modes de fusion, écrêtage, verrou alpha, papier (exporté comme calque « Papier »). À la lecture : masques de calque appliqués, compressions brute / RLE / ZIP. Les images d'une animation étant des dossiers, elles passent aussi par le PSD. Non pris en charge : PSD 16/32 bits et PSB, effets de calque Photoshop (ombres, contours), profils couleur ICC (l'image est lue en sRVB, comme dans ibisPaint).

Vérifié avec le lecteur indépendant psd-tools et sur ses fichiers de test Photoshop. Les pinceaux partagés en QR code par la communauté ibisPaint utilisent un format non documenté : ils ne sont pas encore importables.

## Fonctionnalités

| Domaine | Détail |
|---|---|
| Pinceaux | 11 pinceaux (plume G, stylo, feutre, crayon à grain, aérographe, aquarelle, pinceau plat, calligraphie, spray, pixel, néon) + 4 gommes ; taille, opacité, dureté, densité, espacement, grain papier, dispersion, variation, effilement, rondeur/angle, pression → taille / opacité |
| Trait | Stabilisation réglable, lissage par courbes, opacité uniforme par trait, pression du stylet, gomme du stylet reconnue |
| Outils | Pinceau, gomme, doigt (étaler), flou, pot de peinture (tolérance, extension, référence toile/calque, **trames manga** : points, lignes, hachures, grain), dégradé (linéaire/radial), pipette, formes (ligne/rectangle/ellipse, contour ou rempli), **texte** (police, taille, gras, italique, alignement), sélection (rectangle/ellipse/lasso/**baguette magique**, nouvelle/ajouter/retirer, contenu d'un calque), transformation (déplacer, redimensionner, pivoter, miroir), main |
| Calques | Illimités, **dossiers imbriqués** (mode et opacité propres, repliables), **glisser-déposer** pour réordonner ou ranger, **sélection multiple** (grouper, fusionner, supprimer), 17 modes de fusion, opacité, écrêtage, verrou alpha, dupliquer, fusionner vers le bas / les visibles, aplatir, importer une image, papier coloré ou transparent |
| Toile | Taille de la toile (avec ancrage), redimensionner l'image, recadrer sur la sélection, pivoter de 90° / 180°, retourner |
| Pinceaux perso | **Pinceau à partir d'une image** : le sombre devient l'encre, rotation aléatoire, suivi de la direction |
| Référence | **Fenêtre de référence** flottante (déplacer, redimensionner, zoomer) ; la toucher prélève la couleur |
| Animation | **Mode animation** façon ibisPaint : chaque dossier de premier niveau est une image ; bande d'images avec miniatures, lecture, **pelures d'oignon** (rouge avant, vert après), 2 à 24 images/s, **export GIF** |
| Timelapse | **Enregistrement automatique** du dessin (pastille REC), export en **GIF** |
| Filtres (17) | Luminosité/contraste, TSL, balance des couleurs, niveaux de gris, sépia, négatif, postérisation, seuil, flou gaussien, flou de mouvement, netteté, lueur, mosaïque, bruit, vignette, **extraction du trait**, détection de contours — avec aperçu en direct |
| Règles | Symétrie miroir vertical/horizontal/4 quarts, radiale ×6/×8/×12, kaléidoscope ; **règles** parallèle (hachures), concentrique, rayonnante (lignes de vitesse), **perspective à 1 point** ; centres et angle déplaçables à la main |
| Navigation | Pincer pour zoomer et pivoter, tap 2 doigts = annuler, 3 doigts = rétablir, appui long = pipette, molette, Espace + glisser, miroir de la vue |
| Historique | Annuler / rétablir illimité (dans un budget mémoire) |
| Galerie | Œuvres enregistrées automatiquement (`.mpaint` = ZIP de PNG + manifeste), miniatures, renommer, dupliquer, exporter, supprimer |
| Export | PNG (transparence conservée si le papier est masqué), JPEG, PSD à calques et dossiers, GIF animé (animation, timelapse) |

## Structure

```
MortisPaint/            Code partagé (moteur + interface)
  Engine/               Moteur de peinture indépendant de l'UI (calques et dossiers, pinceaux, composition, filtres, règles, texte, trames,
                        historique, animation, timelapse, encodeur GIF, formats .mpaint et PSD)
  Controls/             Toile interactive, roue chromatique, fenêtre de référence, icônes, thème
  Editor/               État de l'éditeur (outil, pinceaux, couleurs)
  Views/                Galerie, éditeur responsive, fenêtre principale
  Services/             Galerie et préférences sur disque
MortisPaint.Desktop/    Lanceur Windows (et Linux / macOS)
MortisPaint.Android/    Lanceur Android
```

## Lancer sur PC

```bash
dotnet run --project MortisPaint.Desktop
```

## Compiler pour Android

Prérequis (une seule fois) : la charge de travail Android de .NET, puis le SDK Android et un JDK.

```bash
dotnet workload install android
```

```bash
dotnet build MortisPaint.Android -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True
```

Téléphone branché en USB (débogage USB activé) :

```bash
dotnet build MortisPaint.Android -t:Run -f net10.0-android
```

APK installable :

```bash
dotnet publish MortisPaint.Android -c Release -f net10.0-android
```

L'APK se trouve dans `MortisPaint.Android/bin/Release/net10.0-android/publish/`.

## Raccourcis clavier

`B` pinceau · `E` gomme · `S` doigt · `R` flou · `G` pot · `D` dégradé · `I` pipette · `U` formes · `T` texte · `M` sélection · `V` transformer · `H` main · `Ctrl+G` grouper · `,` / `.` image précédente / suivante · `[` `]` taille · `X` échanger les couleurs · `F` miroir de la vue · `Ctrl+Z` / `Ctrl+Y` annuler / rétablir · `Ctrl+S` enregistrer · `Ctrl+E` exporter PNG · `Ctrl+Maj+E` exporter PSD · `Ctrl+I` importer une image · `Ctrl+A` / `Ctrl+D` sélection · `Suppr` effacer · `Entrée` valider · `Échap` annuler · `Ctrl+0` ajuster · `Ctrl+Maj+N` nouveau calque · `Ctrl+J` dupliquer le calque · molette zoom · `Ctrl`+molette taille · `Alt`+molette rotation · clic droit pipette.
