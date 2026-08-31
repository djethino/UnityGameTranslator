# RTLTMPro — fichiers vendorisés (tables et shaping arabe)

Source : https://github.com/pnarimani/RTLTMPro — licence MIT (copie : `LICENSE-RTLTMPro.txt`,
à CONSERVER, y compris dans la release — l'avis MIT doit suivre le code).
Épinglé au commit `f480419bbbffed1be3c129d68cc0182afcfbcac3` (master du 2026-04-29, v4.0.0).

**Fichiers repris TELS QUELS** (namespace `RTLTMPro` conservé pour pouvoir diff l'amont) :
`GlyphTable`, `GlyphFixer`, `TashkeelFixer`, `TashkeelLocation`, `TextUtils`, `Char32Utils`,
`FastStringBuilder`, `Types/*` — ~95 % de données Unicode que réécrire serait recopier Unicode à
la main (analyse/issue-24-rtl-second-look.md, §7.1).

**Volontairement NON repris** :
- `LigatureFixer` — c'est notre étape C (bidi par runs) : elle doit connaître NOS placeholders
  `[!v*N]`/`[!STR*N]` et nos balises (décision D7 du 06/08) ;
- `RichTextFixer` — même raison ;
- `RTLSupport` — le câblage d'ensemble, remplacé par notre pipeline ;
- `RTLTextMeshPro*` / `Editor` — dépendent d'Unity, on a nos propres points d'injection.

⚠ Les buffers de `TashkeelFixer` et les listes de `GlyphFixer` sont STATIQUES et partagés :
appel main-thread uniquement — la façade `PresentationFormsShaper` le documente et nos workers
ne doivent jamais l'appeler (piège n°5 de l'analyse du 06/08).

⚠ Toute retouche locale à ces fichiers doit être notée ICI (aucune à ce jour).
