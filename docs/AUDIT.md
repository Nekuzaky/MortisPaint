# Audit MortisPaint — 21/10/2026

Sept audits en lecture seule, un par zone fonctionnelle, sur la branche `feat/mortis-style-et-outils`. Les constats en double entre zones sont fusionnés. **Vérifié** = relu dans le code par moi ; **reproduit** = reproduit par un test. Les autres constats viennent des audits et restent à reproduire.

Gravité : **Bloquant** (plantage, perte de données, fonction inopérante) · **Majeur** (résultat faux, perte possible, gros coût) · **Mineur**.

## Corrections (branche `fix/audit`)

Tous les constats ci-dessous sont corrigés. Avant chaque correction, un test a reproduit le défaut sur le code d'origine ; après la correction, ce même test passe. Sur le code intégré, toutes les suites passent :

| Suite | Contenu | Résultat |
|---|---|---|
| Harnais de l'interface (Avalonia.Headless) | scénario complet, glisser-déposer des calques, 9 tests des vues | tout passe |
| Gestes de la toile | stylet et paume, pincements, outils au doigt, Maj, verrou | 83 scénarios |
| Moteur de pinceau | symétrie, plafond d'opacité, espacement, flou, règle | 36 vérifications |
| Calques | structure, rendu, annuler/rétablir, 6000 opérations aléatoires | 100 vérifications |
| Filtres et texte | verrou alpha, bords, aperçu asynchrone, polices de repli | 45 vérifications |
| Animation et timelapse | GIF, palette, cadence, plafond, miniatures | tout passe |
| Fichiers | PSD piégés (10 000 fichiers abîmés), aller-retours, enregistrement atomique, EXIF, galerie | tout passe |

Les 19 PSD de test de psd-tools donnent un rendu identique, à l'octet près, à celui d'avant les corrections.

Restent à savoir :
- **Clavier Android.** La marge qui garde le panneau Texte visible au-dessus du clavier n'a pas été essayée sur un appareil.
- **Groupe « pass-through » à moins de 100 %.** Quand on le fusionne, le résultat est approché : l'opacité du dossier est appliquée à chaque calque.
- **Rotation de 90°.** Elle remplace le document, donc elle vide l'historique (après confirmation). Le demi-tour, lui, passe par l'historique.
- **PSD aplati RVB + 1 canal sans ressource 1053.** Il est lu comme transparent, comme le fait psd-tools. psd-tools n'étant pas installé, ces règles ont été appliquées de mémoire.

## Bloquants (5)

| # | Zone | Défaut | Où | Statut |
|---|---|---|---|---|
| B1 | Calques | Le glisser-déposer ne déplace jamais rien : `Capture(null)` déclenche `PointerCaptureLost` → `FinishDrag(false)` avant le commit. | `EditorView.Layers.cs:366-372` | **reproduit** |
| B2 | Interface | Plantage natif environ 300 ms après la fermeture de l'éditeur : le minuteur des miniatures lit des bitmaps déjà libérés (ex. importer une image, puis ←). | `EditorView.axaml.cs:226`, `Layers.cs:160` | vérifié (plantage reproduit par l'audit) |
| B3 | Fichiers | Supprimer une œuvre efface récursivement `Timelapse/<Id>` avec l'Id **brut** du manifeste : un `.mpaint` avec `"Id":".."` ou un chemin absolu efface toute la galerie ou un dossier arbitraire. | `AppServices.cs:82-83` | vérifié |
| B4 | Toile | Recadrer sur une sélection hors de la toile : `ArgumentException` dans un `async void` → plantage. | `EditorView.Tools.cs:465`, `CanvasOps.cs:84` | vérifié |
| B5 | Toile / pinceau | Un 2ᵉ pointeur (paume, doigt) pendant un trait au stylet écrase la session sans la terminer : trait perdu, empreintes laissées dans le tampon puis réinjectées dans un trait ultérieur ; quand la paume se relève, `_gesture` repasse à None. | `CanvasView.cs:531-572, 644, 897-904` | vérifié (2 audits) |

## Moteur de pinceau

- **Majeur — Symétrie : angle fixe non reflété.** Sans « Suivre la direction », la pointe garde `_b.Angle` au lieu de l'angle transformé : la calligraphie en miroir n'est pas symétrique. `DabRenderer.cs:113, 145` → utiliser `angle`.
- **Majeur — Pointe carrée tournée : zone sale trop petite** (r+2 au lieu de r·√2) → coins coupés et restes dans le tampon. `DabRenderer.cs:145-165`.
- **Majeur — Doigt et flou ignorent le verrou alpha** : la couleur s'étale dans le transparent. `Sessions.cs:246-251, 307-313`.
- **Majeur — Espacement minimal 0,4/zoom** : le trait dépend du zoom (pointillés pour un trait fin tracé dézoomé). `StrokePath.cs:130`.
- **Majeur — « Pression → opacité » sature** : l'alpha s'accumule empreinte par empreinte (20 % de pression ≈ 84 % d'opacité). `DabRenderer.cs:59`.
- Mineurs :
  - 1ʳᵉ empreinte d'un pinceau plat à 0° même avec « Suivre la direction » ;
  - doigt au bord de la toile qui troue un fond opaque ;
  - flou en O(d³) avec 7 Mo alloués par empreinte à 400 px ;
  - doigt et flou à diamètre fixe malgré « Pression → taille » ;
  - règle concentrique appliquée avant la stabilisation ;
  - effilement non pris en compte dans l'espacement ;
  - pointe image depuis un PNG blanc limitée à 25 %.

## Calques, dossiers, composition, historique

- **Majeur — Glisser sur un dossier qui ne contient que le calque glissé** : la plage est calculée après le retrait → insertion tout en bas, dossier parent rendu non contigu (et `DeleteFolder` supprime alors d'autres calques). `PaintEngine.Advanced.cs:141-158`.
- **Majeur — « Fusionner les calques visibles » coupe un dossier en deux** (calque fusionné posé à la racine au milieu du dossier). `Advanced.cs:215-221`.
- **Majeur — Fusion vers le bas d'un calque écrêté en mode non Normal** : l'alpha de la base est mis au carré, les bords semi-transparents s'éclaircissent partout. `PaintEngine.cs:189-202`.
- **Majeur — Fusion vers le bas entre dossiers** : en animation, « Fusionner » colle l'image 2 dans l'image 1. `PaintEngine.cs:177-181` → refuser si les dossiers diffèrent.
- **Majeur — Fusion de calques écrêtés / de dossier** : le résultat n'est plus écrêté, les sous-dossiers masqués sont ignorés. `Advanced.cs:187-194`.
- **Majeur — Mémoire de l'historique sous-estimée** : les calques supprimés ou fusionnés gardés en vie ne sont pas comptés (risque de manque de mémoire sur Android) ; « Retourner la toile » stocke 2 copies pleines par calque. `History.cs:103, 183`.
- **Majeur — Échantillonnage en animation** : le pot et la pipette « Toile » voient toutes les images ; la baguette voit les pelures d'oignon. `PaintEngine.cs:345, 383`, `Advanced.cs:325` → `new FrameFilter { Frame = CurrentFrame }`.
- Mineurs :
  - édition d'opacité en attente (600 ms) non vidée par Ctrl+J, Ctrl+Maj+N, l'œil de dossier et le papier ;
  - Annuler replie ou déplie les dossiers (`Expanded` restauré) ;
  - grouper une sélection qui couvre un sous-dossier le dissout ;
  - Aplatir et Fusionner visibles en animation superposent toutes les images.

## Toile et outils

- **Majeur — Outils instantanés au premier doigt** : pot, baguette, texte et pipette agissent avant qu'un pincement ou un tap à 2 doigts soit reconnu (pincer avec le pot remplit la zone). `CanvasView.cs:909-937`.
- **Majeur — Transformation identité validée** : choisir V puis changer d'outil pousse une entrée pleine toile (128 Mo en 4000²) et évince l'historique. `Sessions.cs:503-514`.
- **Majeur — Commandes qui contournent `CommitPending`** (transformation ou filtre en cours) : Retourner la toile, Remplir, Sélectionner le contenu, Ctrl+J, Ctrl+T, Ctrl+Maj+N → calque à moitié retourné, filtre non validé qui reparaît. `EditorView.axaml.cs:536-600, 766-769`.
- **Majeur — Baguette lente sur grande zone bruitée** (rectangles par segment + `Simplify` sur le fil d'interface). `Selection.cs:73-108`.
- Mineurs :
  - poignées de règle et de symétrie qui volent un trait commencé à moins de 22 px ;
  - sélections dégénérées (hauteur nulle) qui bloquent toute peinture ;
  - Maj sert à la fois à ajouter et à contraindre en carré ;
  - sélection hors historique ;
  - Ctrl+Z ou V en plein trait ;
  - pincement figé avec 3 doigts ;
  - échelle de transformation qui peut tomber à 0 ;
  - `SpacePan` bloqué si le relâchement d'Espace est perdu.

## Fichiers, formats, galerie

- **Majeur — Perte silencieuse à la fermeture** : si `AutoSave` échoue (stockage plein, manque de mémoire), le toast meurt avec l'éditeur et le document est libéré. `MainView.axaml.cs:94-101`, `EditorView.axaml.cs:216`.
- **Majeur — Un `.mpaint` importé n'est jamais enregistré** (`IsDirty` reste faux). `GalleryView.axaml.cs:360-365`. *(vérifié)*
- **Majeur — Tailles lues dans le fichier non bornées** (rectangle de calque PSD, nombre de canaux, nombre de calques, PNG du ZIP) : un PSD de 200 octets peut demander 8 Go. `PsdFile.cs:354-357, 412-419, 533`, `ProjectIO.cs:178-205`.
- **Majeur — Exceptions non rattrapées dans des handlers `async void`** : suppression avec un JPEG verrouillé, export avec disque plein, export du timelapse. `GalleryView.axaml.cs:245, 308`, `EditorView.axaml.cs:287`.
- **Majeur — Groupes PSD « pass » (pass-through) rendus isolés** : les ombres en Produit d'un groupe Photoshop deviennent opaques. `PsdFile.cs:383`.
- Mineurs :
  - cycle de dossiers A→B→A dans un `.mpaint` = boucle infinie ;
  - 4ᵉ canal d'un PSD aplati pris pour de la transparence ;
  - calque « Papier » ajouté à chaque aller-retour PSD ;
  - PSD construit entièrement en mémoire (échec au-delà de 2 Go, export sur le fil d'interface) ;
  - orientation EXIF des photos ignorée ;
  - `settings.json` écrit sans atomicité ;
  - timelapses orphelins jamais nettoyés.

## Animation, timelapse, GIF

- **Majeur — Changer de calque ne redessine pas la toile en animation** (`SetActive` n'invalide pas) → mélange de 2 images à l'écran. `PaintEngine.cs:88-93`. *(vérifié)*
- **Majeur — Impossible de revenir à l'image 1 depuis un calque de fond** (`GoToFrame` compare au repli de `CurrentFrame`). `Advanced.cs:233, 267`.
- **Majeur — Export GIF de l'animation** : toutes les images composées en pleine résolution et gardées en mémoire sur le fil d'interface. `EditorView.Animation.cs:234`.
- **Majeur — Une erreur arrête le timelapse pour toute la session** (`await previous` hors du `try`, seul `IOException` attrapé, `bmp` qui fuit). `Timelapse.cs:39, 57`.
- **Majeur — 2 compositions pleine résolution après chaque trait** (miniature d'image + capture du timelapse), et `RebuildTimeline` recrée tous les boutons. `Animation.cs:146`, `EditorView.axaml.cs:66`.
- **Majeur — Le dossier du timelapse grossit sans limite** (un JPEG par action, jamais élagué). `Timelapse.cs:33`.
- Mineurs :
  - clé des miniatures qui ignore le fond et les propriétés ;
  - `OnionSkin=false` enregistré si on ferme pendant la lecture ;
  - captures du timelapse qui montrent les pelures d'oignon ;
  - numérotation écrasée après un trou ;
  - `Clear` non synchronisé ;
  - images déformées après un changement de taille de toile ;
  - palette GIF au centre des cases (le blanc devient 252) ;
  - cadence arrondie (24 i/s → 25).
- Vérifié correct : structure GIF et LZW (Pillow les décode), pelures aux bords, `DuplicateFrame` avec sous-dossiers.

## Filtres, trames, texte

- **Majeur — Verrou alpha des filtres** : `DstIn` met l'alpha au carré sur toute la toile, hors sélection et hors historique. `Filters.cs:438-444`.
- **Majeur — Aperçu de filtre lent** : toute la toile, sur le fil d'interface (demi-teinte, flou de mouvement : plusieurs secondes sur Android). `Filters.cs:423-446`.
- **Majeur — Bords transparents** après un flou gaussien ou un flou de mouvement (`Decal`). `Filters.cs:136, 289`.
- **Majeur — Recadrage trop grand de 4 px** (marge de 2 px de `Selection.Bounds`). `Tools.cs:474`.
- **Majeur — Raccourcis actifs quand le focus a quitté le champ texte** : Retour arrière efface le calque texte qu'il vient de créer. `EditorView.axaml.cs:743, 796`. *(2 audits)*
- **Majeur — La désactivation Android valide sans prévenir** le filtre, le texte ou la transformation en cours. `MainView.axaml.cs:49`.
- **Majeur — Taille de toile jusqu'à 8192²** sans garde mémoire (la galerie limite à 6000). `Tools.cs:414`.
- Mineurs :
  - liseré autour des sélections filtrées ;
  - bande effacée par la mosaïque ;
  - demi-teinte trop sombre ;
  - aperçu du texte qui hérite de l'opacité du calque ;
  - pas de police de repli (japonais, emoji) ;
  - zoom bloqué pendant un filtre ;
  - un filtre neutre crée quand même une entrée d'historique.

## Interface

- **Majeur — Fenêtre de référence hors écran** après rotation ou redimensionnement (position bornée seulement pendant le glisser). `Reference.cs:37-64`.
- Mineurs :
  - Maj+clic sélectionne des calques cachés dans des dossiers repliés ;
  - abonnements de `MainView` jamais retirés ;
  - images (miniatures, référence 2048²) jamais libérées ;
  - bouton Référence qui reste actif après Retour ;
  - saut de l'image de référence après un pincement ;
  - pas de noms d'accessibilité ;
  - « Réinitialiser » sans effet sur un pinceau image ;
  - clavier Android qui pourrait masquer le panneau Texte (Android 15, à confirmer).

## Ce qui a été vérifié correct

Matrice de vue (zoom, rotation, miroir, pincement) ; gestes tactiles (taps 2 et 3 doigts, appui long) ; contiguïté des dossiers pour la plupart des opérations ; restauration par `DocState` ; libération des calques par l'historique ; composition (écrêtage sur dossier, dossiers masqués, pelures) ; matrices de `CanvasOps` ; conformité du PSD écrit ; ordre BGRA/RGBA ; `MapRgb` ; trames ; encodeur GIF ; `ReplaceDocument` (pas de closure sur l'ancien moteur) ; disposition responsive ; ordre de `HandleBack`.
