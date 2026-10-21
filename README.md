# MortisPaint

Application de dessin fait main, inspirée d'ibisPaint (fonctions « premium » comprises), écrite en C# avec **Avalonia 12** et **SkiaSharp 3**. Une seule base de code pour **Windows (PC)** et **Android**, avec une interface responsive :

- **Grand écran** (≥ 760 px) : rail d'outils à gauche, panneau Pinceau / Couleur / Calques à droite.
- **Téléphone** : barre d'outils en bas, panneaux en tiroir, curseurs taille / opacité sur le côté (comme ibisPaint).

## Style « Carnet papier »

Interface claire reprise de la variante papier du design system Mortis : fond crème `#F6F5F2`, encre graphite `#2A2C30`, un seul rouge (Ruban `#B4321E`) réservé à la sélection et à l'action principale, filets fins, rayons 4 / 6 / 10 px, aucune lueur ni dégradé. Le plan de travail est un papier pointillé où la toile est posée comme une feuille. Icônes au trait dessinées pour l'app. Polices embarquées (licence OFL, `MortisPaint/Assets/Fonts`) : Schibsted Grotesk (titres), Atkinson Hyperlegible Next (interface), Atkinson Hyperlegible Mono (valeurs).

## Compatibilité ibisPaint

ibisPaint importe et exporte le **PSD à calques** (« PSD (calques conservés) »). MortisPaint lit et écrit ce format :

- **ibisPaint → MortisPaint** : dans ibisPaint, exporter l'œuvre en PSD (calques conservés), puis dans MortisPaint *Ouvrir* le fichier depuis le carnet.
- **MortisPaint → ibisPaint** : menu ☰ → *Exporter en PSD* (ou depuis le carnet), puis importer le PSD dans ibisPaint.

Conservés : calques, noms (Unicode), visibilité, opacité, modes de fusion, écrêtage, verrou alpha, papier (exporté comme calque « Papier »). À la lecture : masques de calque appliqués, dossiers aplatis (leur visibilité et opacité se reportent sur leurs calques), compressions brute / RLE / ZIP. Non pris en charge : PSD 16/32 bits et PSB, effets de calque Photoshop (ombres, contours), profils couleur ICC (l'image est lue en sRVB, comme dans ibisPaint).

Vérifié avec le lecteur indépendant psd-tools et sur ses fichiers de test Photoshop. Les pinceaux partagés en QR code par la communauté ibisPaint utilisent un format non documenté : ils ne sont pas encore importables.

## Fonctionnalités

| Domaine | Détail |
|---|---|
| Pinceaux | 11 pinceaux (plume G, stylo, feutre, crayon à grain, aérographe, aquarelle, pinceau plat, calligraphie, spray, pixel, néon) + 4 gommes ; taille, opacité, dureté, densité, espacement, grain papier, dispersion, variation, effilement, rondeur/angle, pression → taille / opacité |
| Trait | Stabilisation réglable, lissage par courbes, opacité uniforme par trait, pression du stylet, gomme du stylet reconnue |
| Outils | Pinceau, gomme, doigt (étaler), flou, pot de peinture (tolérance, extension, référence toile/calque), dégradé (linéaire/radial), pipette, formes (ligne/rectangle/ellipse, contour ou rempli), sélection (rectangle/ellipse/lasso, inverser), transformation (déplacer, redimensionner, pivoter, miroir), main |
| Calques | Illimités, 17 modes de fusion, opacité, écrêtage, verrou alpha, dupliquer, fusionner, monter/descendre, aplatir, importer une image, papier coloré ou transparent |
| Filtres (17) | Luminosité/contraste, TSL, balance des couleurs, niveaux de gris, sépia, négatif, postérisation, seuil, flou gaussien, flou de mouvement, netteté, lueur, mosaïque, bruit, vignette, **extraction du trait**, détection de contours — avec aperçu en direct |
| Règles | Symétrie miroir vertical/horizontal/4 quarts, radiale ×6/×8/×12, kaléidoscope |
| Navigation | Pincer pour zoomer et pivoter, tap 2 doigts = annuler, 3 doigts = rétablir, appui long = pipette, molette, Espace + glisser, miroir de la vue |
| Historique | Annuler / rétablir illimité (dans un budget mémoire) |
| Galerie | Œuvres enregistrées automatiquement (`.mpaint` = ZIP de PNG + manifeste), miniatures, renommer, dupliquer, exporter, supprimer |
| Export | PNG (transparence conservée si le papier est masqué), JPEG, PSD à calques |

## Structure

```
MortisPaint/            Code partagé (moteur + interface)
  Engine/               Moteur de peinture indépendant de l'UI (calques, pinceaux, composition, filtres, historique, format .mpaint)
  Controls/             Toile interactive, roue chromatique, icônes
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

`B` pinceau · `E` gomme · `S` doigt · `R` flou · `G` pot · `D` dégradé · `I` pipette · `U` formes · `M` sélection · `V` transformer · `H` main · `[` `]` taille · `X` échanger les couleurs · `F` miroir de la vue · `Ctrl+Z` / `Ctrl+Y` annuler / rétablir · `Ctrl+S` enregistrer · `Ctrl+E` exporter PNG · `Ctrl+Maj+E` exporter PSD · `Ctrl+I` importer une image · `Ctrl+A` / `Ctrl+D` sélection · `Suppr` effacer · `Entrée` valider · `Échap` annuler · `Ctrl+0` ajuster · `Ctrl+Maj+N` nouveau calque · `Ctrl+J` dupliquer le calque · molette zoom · `Ctrl`+molette taille · `Alt`+molette rotation · clic droit pipette.
