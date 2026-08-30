# Rider signale des erreurs de trim et d'AOT que la compilation ne voit pas

Rider signale douze diagnostics `IL2026` et `IL3050` en sévérité ERROR, deux
dans `Program.cs` sur `AddOptions<HubOptions>().BindConfiguration(...)` et dix
dans `Api/ApiEndpoints.cs` sur les appels `MapGet` et `MapPost`. Ce sont des
faux positifs. N'agissez pas dessus, et ne les faites pas taire.

## Pourquoi la compilation n'est pas d'accord

Le projet pose `PublishAot=true`, donc
`Microsoft.NET.Sdk.Analyzers.targets` active `EnableAotAnalyzer` et
`EnableTrimAnalyzer`. Ce sont de simples groupes de propriétés, évalués à chaque
compilation et pas seulement à la publication, et `AnalysisLevel=latest` empêche
le repli sur un niveau d'analyse bas de les remettre à zéro. La même condition
sur `PublishAot` active aussi les deux générateurs de source qui comptent ici,
dans `Microsoft.NET.Sdk.FrameworkReferenceResolution.targets` :

- `EnableRequestDelegateGenerator`, pour les endpoints d'API minimale,
- `EnableConfigurationBindingGenerator`, pour la liaison des options.

Chaque générateur émet un intercepteur qui remplace la surcharge annotée, et
c'est ce remplacement qui fait disparaître le diagnostic. Roslyn applique
l'interception, donc l'analyseur voit l'appel généré. Le moteur de Rider
rapporte la surcharge annotée à la place.

Trois faits, pris ensemble, montrent que ces diagnostics ne se déclenchent
jamais dans une compilation réelle. `TreatWarningsAsErrors=true` est posé sur
tout l'arbre. La compilation de CI lance
`dotnet build PerfSentinelHub.sln -c Release --warnaserror` et passe. Le job
NativeAOT smoke lance une publication AOT complète, passe, et son log ne porte
ni `IL2026` ni `IL3050`.

## Pourquoi ils ne sont pas supprimés

`NoWarn` atteindrait le compilateur, pas seulement l'éditeur, et désactiverait
l'analyseur qui garde la compilation AOT. Une vraie régression AOT arriverait
alors jusqu'au job smoke, ou jusqu'au binaire publié, au lieu du compilateur. Le
bruit dans un éditeur coûte moins cher que la perte du contrôle.

## Revérifier après un bump du SDK

Publiez pour un identifiant de runtime et confirmez que le log reste propre :

```
make publish TARGETARCH=x64
```

Un diagnostic qui survit à cette commande est réel, et sa place est dans le code
plutôt que dans ce document.
