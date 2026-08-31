# RichTextKit — UAX#9 vendorisé (étape C, bidi)

Source : https://github.com/toptensoftware/RichTextKit — licence Apache-2.0
(copie : `LICENSE-RichTextKit.txt`, à CONSERVER, release comprise).
Épinglé au commit `c215125dbf23f521956d051619e19bd2ecd472f8` (main, dernier push 2026-07).

**Repris** (namespace `Topten.RichTextKit` conservé pour diff amont) : `BidiAlgorithm/`
(l'algorithme UAX#9 complet, décision D2 du 06/08), `Unicode/{UnicodeTrie, Directionality,
PairedBracketType}`, `Utils/{Slice, MappedSlice, Buffer, BiDictionary, ArrayEnumerator,
BinarySearchExtension, BinaryReaderExtensions, SwapHelper}`, `Resources/BidiClasses.trie`
(données Unicode compilées, embarquées sous leur nom logique d'origine
`Topten.RichTextKit.Resources.BidiClasses.trie` — le code de chargement reste intact).
⚠ La liste du 06/08 excluait `BiDictionary` : faux, `Bidi.cs` s'en sert (appariement des
crochets). Vérifié par grep de clôture : le lot ne référence rien d'autre.

**Volontairement NON repris** : `Utf32Buffer`/`Utf32Utils` (seuls porteurs de `Span`, inutiles —
notre étape C fournit ses codepoints), `ObjectPool`, `RunExtensions`, `UndoManager`,
~~`UnicodeTrieBuilder`~~ (d'abord exclu comme « construction seulement » — faux : `UnicodeTrie.Get` lit ses constantes de format dedans, il est donc repris tel quel, partie constructeur inutilisée comprise), `InternalsVisibleTo`, les
classes LineBreak/WordBoundary/GraphemeCluster (autres tries, autres sujets).

**Retouches locales** (les seules) :
1. `Utils/Slice.cs` — méthode `AsSpan()` supprimée (netstandard2.0 sans System.Memory ; aucun
   appelant dans le lot) ;
2. `Unicode/UnicodeClasses.cs` — réécrit élagué : charge le SEUL trie bidi, depuis
   `typeof(UnicodeClasses).Assembly` (l'original nommait `LineBreaker`, hors lot, et chargeait
   4 tries dont 3 pour d'autres sujets).

Les suites de conformité Unicode officielles (`BidiTest.txt`, `BidiCharacterTest.txt`) sont dans
`tests/UnityGameTranslator.Core.Checks/TestData/*.gz` (gzippées : 15 Mo → 1,7 Mo) — elles NE sont
PAS livrées, elles font tourner les contrôles.
