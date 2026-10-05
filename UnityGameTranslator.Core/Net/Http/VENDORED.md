# Net/Http — le client HTTP du mod, porté de Mono (issue #31)

Pourquoi il existe : un jeu peut livrer un `System.Net.Http.dll` qui ne marche pas sur son propre
runtime (une copie .NET Framework de Microsoft appelle `System.Net.Logging.get_On`, absent du
`System.dll` de Mono) — toutes les requêtes du mod mouraient. Remplacer la bibliothèque du jeu
changerait ce que le JEU charge : décision de l'utilisateur (2026-10-05), le mod a son propre client
et ne touche à rien du jeu.

Source : https://github.com/Unity-Technologies/mono — branche `2019.4-branch-updates`, commit
`d9ad36f4a70e55923604172a47f2223f4f6df5b6` (2020-06-08), `mcs/class/System.Net.Http`. C'est le code du
`System.Net.Http.dll` livré par Unity de 2018.1 à 2020.3 (handler sur `HttpWebRequest`). Licence MIT
(bibliothèques de classes de Mono) ; en-têtes de licence conservés dans chaque fichier ; notice dans
`THIRD_PARTY_LICENSES.md`.

## Qui s'en sert

- **Mono** : `WebRequestTransport`, sur le `HttpWebRequest` public du runtime. Tout ce qu'il nomme
  du runtime existe et est public de Unity 2018.1 à 6000.6, signatures comprises (mesuré le
  2026-10-05 sur les bibliothèques complètes de dix versions).
- **IL2CPP** : `Shared/PlatformHttpTransport.cs` (adaptateurs IL2CPP), sur le `HttpClient` du .NET
  du loader — là-bas `HttpWebRequest` ouvre une connexion par requête.
- Le choix est fait par l'adaptateur (`IModLoaderAdapter.HttpTransport`), posé par
  `TranslatorCore.Initialize`. Pas de transport par défaut.

## Repris, retiré, modifié

**Repris** (espace de noms `System.Net.Http` → `UnityGameTranslator.Net.Http`) : `HttpClient`,
`HttpMessageInvoker`, `HttpMessageHandler`, `DelegatingHandler`, `HttpRequestMessage`,
`HttpResponseMessage`, `HttpMethod`, `HttpRequestException`, `HttpCompletionOption`, `HttpContent`,
`ByteArrayContent`, `StringContent`, `StreamContent` ; en-têtes : `HttpHeaders`, `HttpRequestHeaders`,
`HttpResponseHeaders`, `HttpContentHeaders`, `HttpHeaderValueCollection`, `HeaderInfo`,
`HttpHeaderKind`, `Lexer`, `Parser`, `CollectionParser`, `CollectionExtensions`, `HashCodeCalculator`,
`AuthenticationHeaderValue`, `EntityTagHeaderValue`, `MediaTypeHeaderValue`, `NameValueHeaderValue`,
`NameValueWithParametersHeaderValue`, `TransferCodingHeaderValue`.

**Non repris** (le mod ne s'en sert pas) : `FormUrlEncodedContent`, `MultipartContent`,
`MultipartFormDataContent`, `MessageProcessingHandler`, `ClientCertificateOption`, les variantes
Android/Mac/« platform not supported » ; types de valeurs `CacheControl`, `ContentDisposition`,
`ContentRange`, `MediaTypeWithQuality`, `Product`, `ProductInfo`, `QualityValue`, `RangeCondition`,
`Range`, `RangeItem`, `RetryCondition`, `StringWithQuality`, `TransferCodingWithQuality`, `Via`,
`Warning` ; lecteurs `Parser.EmailAddress` et `Parser.MD5`.

**Modifications** (toutes marquées `UGT:` dans le code) :

1. `HttpHeaders` — les en-têtes dont le type de valeur n'est pas repris gardent leur NATURE
   (requête / réponse / contenu) dans `kind_only_headers` et sont stockés en texte ; `KindOf`.
2. `HttpRequestHeaders`, `HttpResponseHeaders`, `HttpContentHeaders` — seules les propriétés typées
   utilisées restent ; le reste s'atteint par nom.
3. `HttpClient` — seules les surcharges appelées restent ; plus `partial`.
4. `HttpResponseMessage.ReasonPhrase` — repli sur le nom du code au lieu de
   `System.Net.HttpStatusDescription` (interne à `System.dll`).
5. `StreamContent(Stream, CancellationToken)` — public (le transport IL2CPP est compilé hors du Core).
6. `HttpContent.CopyTo` — retiré (ne servait qu'à `ResendContentFactory`).
7. `HttpClientHandler` — ne garde que les réglages ; envoie par `HttpTransport.Current`.
8. `WebRequestTransport` — le code d'envoi de l'ancien `HttpClientHandler`, où les six membres
   **internes** de `System.dll` (accessibles à l'assembly ami signé « System.Net.Http », plus à nous)
   sont remplacés par l'API publique :

   | interne amont | public ici |
   |---|---|
   | `new HttpWebRequest(uri)` | `WebRequest.CreateHttp(uri)` |
   | `ThrowOnError = false` | `catch (WebException) when (Response is HttpWebResponse)` → réponse normale |
   | `WebHeaderCollection.AddInternal` | propriétés pour les en-têtes réservés (`ReservedHeaders`), `Headers.Add` sinon ; un réservé sans propriété est refusé par nom |
   | `ResendContentFactory` | `AllowWriteStreamBuffering = AllowAutoRedirect` : copie gardée seulement quand une redirection peut demander de renvoyer le corps ; sinon le corps reste en flux (garde par morceaux de `ApiClient.StallGuardHandler`) |
   | `ServicePointManager.CloseConnectionGroup` | `ServicePoint.CloseConnectionGroup` sur chaque point de service utilisé |
   | `HttpStatusDescription.Get` | voir 4 |

   Et : le `catch` d'annulation reconnaît le jeton du mod (`when (cancellationToken.IsCancellationRequested)`)
   au lieu du statut `RequestCanceled`.

⚠ Ces membres internes PASSERAIENT sous Mono (notre DLL porte `[module: UnverifiableCode]`, voir
`Shared/IgnoresAccessChecks*.cs`) : on ne s'appuie volontairement ni sur ce contournement, ni sur des
membres internes que rien ne promet de garder.

⚠ Les en-têtes que le mod n'envoie pas (Date, Range, If-Modified-Since, Expect…) sont posés dans des
méthodes à eux : Mono compile une méthode en entier, et un `System.dll` élagué peut manquer d'un
membre qu'eux seuls nomment.

## Contrôles

`tests/UnityGameTranslator.Core.Checks/OwnHttpClientChecks.cs` : aucun fichier du Core ne nomme
`System.Net.Http` ; séquences réelles contre un serveur local qui enregistre les octets reçus. ⚠ Ils
tournent sur le `HttpWebRequest` de CoreCLR, qui ACCEPTE `User-Agent` dans `Headers` là où Mono le
refuse — d'où la liste `ReservedHeaders`, tenue contre `WebHeaderCollection.IsRestricted`. Ce qui
reste propre à Mono se vérifie en jeu.
