# Rider reports trim and AOT errors the build does not

Rider flags twelve `IL2026` and `IL3050` diagnostics at ERROR severity, two in
`Program.cs` on `AddOptions<HubOptions>().BindConfiguration(...)` and ten in
`Api/ApiEndpoints.cs` on the `MapGet` and `MapPost` calls. They are false
positives. Do not act on them, and do not silence them.

## Why the build disagrees

The project sets `PublishAot=true`, so `Microsoft.NET.Sdk.Analyzers.targets`
turns `EnableAotAnalyzer` and `EnableTrimAnalyzer` on. Both are plain property
groups, evaluated on every build rather than only on publish, and
`AnalysisLevel=latest` keeps them from being reset by the low-analysis-level
fallback. The same condition on `PublishAot` also enables the two source
generators that matter here, in
`Microsoft.NET.Sdk.FrameworkReferenceResolution.targets`:

- `EnableRequestDelegateGenerator`, for the minimal API endpoints,
- `EnableConfigurationBindingGenerator`, for the options binding.

Each generator emits an interceptor that replaces the annotated overload, which
is what removes the diagnostic. Roslyn applies the interception, so the analyzer
sees the generated call. Rider's engine reports the annotated overload instead.

Three facts, taken together, show the diagnostics never fire in a real
compilation. `TreatWarningsAsErrors=true` is set for the whole tree. The CI
build runs `dotnet build PerfSentinelHub.sln -c Release --warnaserror` and
passes. The NativeAOT smoke job runs a full AOT publish, passes, and its log
carries no `IL2026` or `IL3050`.

## Why they are not suppressed

`NoWarn` would reach the compiler, not just the editor, and would disable the
analyzer that guards the AOT build. A genuine AOT regression would then reach
the smoke job, or the published binary, instead of the compiler. The noise in
one editor is cheaper than losing the check.

## Re-checking after an SDK bump

Publish for a runtime identifier and confirm the log stays clean:

```
make publish TARGETARCH=x64
```

A diagnostic that survives that command is real, and belongs in the code rather
than in this document.
