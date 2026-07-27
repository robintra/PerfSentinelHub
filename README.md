# PerfLintHub

Long-term memory for [perf-sentinel](https://github.com/robintra/perf-sentinel) findings, and
the single endpoint IDE plugins talk to.

Daemons retain findings in a bounded ring buffer and CI runners disappear with the job. The hub
persists what they produce, keyed by finding signature, and serves the daemon's own read contract
back, so a plugin cannot tell whether it is talking to one daemon, several behind a hub, or a hub
with no daemon at all.

**Status: scaffold.** Nothing below is implemented yet.

## Requirements

.NET 10 SDK. The project publishes as native AOT, so there is no runtime to deploy.

```bash
dotnet run --project PerfLintHub
```

## Design notes

- Findings are stored as opaque documents. Only the queried fields are indexed.
- ADO.NET on `Microsoft.Data.Sqlite`, not EF Core, because native AOT forbids runtime reflection.
- Minimal APIs with a source-generated `JsonSerializerContext`, for the same reason.
- The hub is read-only for humans: acknowledgments live in the repository's
  `.perf-sentinel-acknowledgments.toml`, never here.

## License

[GNU Affero General Public License v3.0](LICENSE).

Sending findings to the hub, or reading them from it, places no license obligation on your own
code: applications and IDE plugins reach it over HTTP, which is arm's-length communication rather
than linking. The AGPL covers this hub's own source. Section 13 applies if you modify it and offer
the modified version to others over a network. Running the unmodified source or image does not
trigger it. This is a practical summary, not legal advice.
