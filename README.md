# SaxonMcp

An [MCP](https://modelcontextprotocol.io) server, written in F#, that lets an AI
model compile and run **XSLT 3.0** and **XQuery 3.1** through
[Saxon HE](https://www.saxonica.com/) — specifically **SaxonCS-HE 13**,
Saxonica's native .NET port (no IKVM/JVM involved).

It exposes three tools over stdio:

| Tool | Purpose |
|---|---|
| `xslt_transform` | Compile a stylesheet and transform an optional source document (or invoke a named/default initial template for source-less stylesheets). |
| `xquery_run` | Compile and evaluate a query, optionally against a context document. |
| `saxon_info` | Report the Saxon product/version/edition and supported spec levels. |

Both `xslt_transform` and `xquery_run` **never throw** for XSLT/XQuery syntax or
runtime errors — those are expected outcomes for a tool an AI model is using to
iterate on code. Instead they always return a JSON string:

```json
{
  "success": true,
  "output": "Hello, World!",
  "diagnostics": []
}
```

On failure, `output` is `null` and `diagnostics` lists every compile/runtime
error and warning Saxon reported, each with `severity`, `message`, `errorCode`
(the XPath/XSLT/XQuery error code, e.g. `XPST0003`), and `line`/`column`
location when available — enough for a model to locate and fix the problem
without re-parsing a stack trace.

Pass `checkOnly: true` to either tool to just validate/compile without
executing — useful for a quick syntax check while iterating.

## Project layout

```
src/SaxonMcp/
  SaxonEngine.fs   core Saxon.Api wrapper: compile + run, diagnostics collection
  Tools.fs         MCP tool definitions ([<McpServerTool>]) and JSON shaping
  Program.fs       stdio MCP host bootstrap
```

## Build & run

Requires the .NET 8 SDK (or newer; the project targets `net8.0`, which is what
SaxonCS-HE ships for). The build itself is driven by [FAKE](https://fake.build/)
(F# Make), pinned as a local dotnet tool so the same command works identically
on your machine and on CI — no separately-installed build tool needed.

```bash
dotnet tool restore   # first time only (installs fake-cli from .config/dotnet-tools.json)
dotnet fake build            # Restore + Build (Release), the default
dotnet fake build -t Clean   # wipe bin/obj under src/ and ./publish
dotnet fake build -t Test    # Restore + Build + Test (no-op until a test project exists)
dotnet fake build -t Publish # Restore + Build + Test + `dotnet publish` -> ./publish
```

Targets are defined in [`build.fsx`](build.fsx) and chained
`Restore ==> Build ==> Test ==> Publish`; running a later target pulls in
everything before it. `Test` auto-discovers any `*.Tests.fsproj`/`*.Tests.csproj`
in the repo, so it starts working the moment a test project is added — nothing
in the script needs to change. Any failed step exits non-zero, so it's safe to
call directly from a CI job.

Equivalent raw commands, if you'd rather not go through FAKE:

```bash
dotnet build src/SaxonMcp
dotnet run --project src/SaxonMcp
```

The server speaks MCP over stdio — it's meant to be launched by an MCP client,
not run interactively.

## Configuring an MCP client

Example client config (e.g. Claude Desktop / Claude Code `.mcp.json`):

```json
{
  "mcpServers": {
    "saxon": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/Users/m_r_n/source/repos/SaxonMCP/src/SaxonMcp"]
    }
  }
}
```

Or point at a published build:

```bash
dotnet publish src/SaxonMcp -c Release -o publish
```

```json
{
  "mcpServers": {
    "saxon": {
      "command": "dotnet",
      "args": ["C:/Users/m_r_n/source/repos/SaxonMCP/publish/SaxonMcp.dll"]
    }
  }
}
```

## Notes and limitations

- **Text in, text out.** Stylesheets, queries, and source documents are passed
  as inline strings, not file paths — the server never touches the filesystem
  itself. This keeps the interface simple for a model that already has the
  content in context.
- **Security.** XSLT and XQuery can still read arbitrary local files or make
  network requests via `fn:doc()`, `fn:unparsed-text()`, and `collection()`
  inside the code being run — that's inherent to the languages, the same as
  letting a model execute a script. Only run this server in a context where
  you trust the stylesheets/queries being executed.
- **Parameters/variables are always `xs:string`.** Values passed via
  `parameters` (XSLT) or `variables` (XQuery) are bound as plain strings; a
  stylesheet/query with a differently-typed `as` on its parameter will get an
  automatic-conversion error from Saxon, which will show up in `diagnostics`.
- **Saxon HE**, not EE or PE — so no schema-aware processing, streaming, or
  XSLT/XQuery update. Static/dynamic errors for anything requiring those
  features surface the same way as any other diagnostic.
