# Transport package inspection — 0.2.0

Development/CI tool, referencing only MediaTransport.Host and its public contract. It never loads a
candidate DLL, constructs a factory/HTTP client, accesses credentials or changes application settings.
It does not install/approve a component. No implicit discovery of user directories is performed.

```powershell
dotnet run --project plugin-sdk/TransportInspect -c Release -- --inspect C:\explicit\candidate
dotnet run --project plugin-sdk/TransportInspect -c Release -- --check-files C:\explicit\candidate
dotnet run --project plugin-sdk/TransportInspect -c Release -- --check-archive C:\explicit\candidate.auralis-transport.zip
```

The explicit absolute local path may be a package or a directory containing at most 64 immediate
package directories. UNC, mapped network drives and reparse-point paths are rejected. Metadata-only
inspection is separate from complete declared-file length/SHA-256 verification. Both check API, minimum
Host, win-x64 and the full capabilities required by the current application. A partial-capability package
can be inspected for other requirements through the Host API; it cannot replace the full default here.

Output is bounded JSON with fixed issue codes, safe IDs/digests/counts, `Executed:false`, `Approved:false`
and explicit `NotTested` entries. It does not echo user paths, arbitrary manifest labels or exception text.
An empty root is unsuccessful. Exit codes: 0 compatible (and complete if requested), 1 invalid/missing/
incompatible, 2 invalid arguments, 4 Ctrl+C cancellation. Cancellation cannot forcibly interrupt a blocked
filesystem call; this tool is not a security sandbox or proof of runtime behavior/publisher authenticity.

Format: `transport.component.json`, schema 1, kind `mediaTransport`. See
[Host format](../../Auralis.MediaTransport.Host/README.md). Use the dedicated transport import card, not
the platform/playback importers. No execution option is supplied. Host 0.6.0 exposes explicit-directory approval
storage and approved loading APIs; this tool never calls approval/import/activation or writes receipts.

`--check-archive` explicitly creates an isolated temporary diagnostic store, validates bounded ZIP metadata,
extracts and hashes declared contents, checks compatibility, then disposes the preview and removes only
its exact empty scaffolding. It never uses the application installation directory. Errors/cleanup failure
return fixed failure reports; an unexpected file/link is not recursively removed. A successful report
includes `CheckedArchive:true`, `CheckedFiles:true` and the archive digest, still `Executed:false` and
`Approved:false`. Paths/labels/exception details are omitted. 62 actual CLI-process assertions cover the
directory and archive modes, including temporary cleanup, tamper, traversal, incompatibility and safe reports.
