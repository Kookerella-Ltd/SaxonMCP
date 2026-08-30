/// MCP tool surface exposed to AI clients: run XSLT / XQuery through Saxon HE.
module SaxonMcp.Tools

open System
open System.ComponentModel
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Text.Json
open ModelContextProtocol.Server
open SaxonMcp.SaxonEngine

type DiagnosticJson =
    { severity: string
      message: string
      errorCode: string
      line: Nullable<int>
      column: Nullable<int>
      moduleUri: string }

type ResultJson =
    { success: bool
      output: string
      diagnostics: DiagnosticJson[] }

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

let private toDiagJson (d: Diagnostic) : DiagnosticJson =
    { severity = d.Severity
      message = d.Message
      errorCode = defaultArg d.ErrorCode null
      line = (match d.Line with Some l -> Nullable l | None -> Nullable())
      column = (match d.Column with Some c -> Nullable c | None -> Nullable())
      moduleUri = defaultArg d.ModuleUri null }

let private serialize (r: RunOutcome) : string =
    let dto =
        { success = r.Success
          output = defaultArg r.Output null
          diagnostics = r.Diagnostics |> List.map toDiagJson |> List.toArray }
    JsonSerializer.Serialize(dto, jsonOptions)

let private pairsOf (d: Dictionary<string, string>) : (string * string) list =
    if isNull d then []
    else d |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq

[<McpServerToolType>]
type SaxonTools() =

    [<McpServerTool(Name = "xslt_transform")>]
    [<Description("Compile an XSLT 3.0 stylesheet with Saxon HE and transform an optional XML source document. \
Invokes a named initial template when `initialTemplate` is given (for stylesheets that need no input document), \
otherwise applies templates to `source`, otherwise falls back to the stylesheet's own xsl:initial-template. \
Always returns JSON of the form { success, output, diagnostics[] } - it never throws for XSLT syntax or runtime \
errors, so check `success` and read `diagnostics` (each with severity/message/errorCode/line/column) to fix the \
stylesheet. Set checkOnly=true to only validate the stylesheet without executing it.")>]
    static member XsltTransform
        (
            [<Description("Full text of the XSLT 3.0 stylesheet to compile.")>]
            stylesheet: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("XML source document text to transform. Omit for stylesheets driven purely by an initial template.")>]
            source: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Name of an xsl:template to invoke as the initial template, instead of applying templates to `source`.")>]
            initialTemplate: string,
            [<Optional; DefaultParameterValue(null: Dictionary<string, string>)>]
            [<Description("Global stylesheet parameters as name/value pairs (bound to xsl:param). Values are always passed as xs:string.")>]
            parameters: Dictionary<string, string>,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Serialization method override: xml, html, text, or json. Defaults to the stylesheet's own xsl:output settings.")>]
            outputMethod: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Base URI used to resolve xsl:include/xsl:import and relative doc() calls inside the stylesheet.")>]
            baseUri: string,
            [<Optional; DefaultParameterValue(false)>]
            [<Description("If true, only compile and validate the stylesheet; do not execute it.")>]
            checkOnly: bool
        ) : string =
        runXslt
            stylesheet
            (Option.ofObj source)
            (Option.ofObj initialTemplate)
            (pairsOf parameters)
            (Option.ofObj outputMethod)
            (Option.ofObj baseUri)
            checkOnly
        |> serialize

    [<McpServerTool(Name = "xquery_run")>]
    [<Description("Compile and evaluate an XQuery 3.1 query with Saxon HE, optionally against a context/source XML \
document. Always returns JSON of the form { success, output, diagnostics[] } - it never throws for XQuery syntax \
or runtime errors, so check `success` and read `diagnostics` (each with severity/message/errorCode/line/column) to \
fix the query. Set checkOnly=true to only validate the query without executing it.")>]
    static member XQueryRun
        (
            [<Description("Full text of the XQuery 3.1 query to compile and run.")>]
            query: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("XML document text bound as the query's context item (accessible as '.').")>]
            contextItem: string,
            [<Optional; DefaultParameterValue(null: Dictionary<string, string>)>]
            [<Description("External variable bindings for 'declare variable $x external;' declarations, as name/value pairs. Values are always passed as xs:string.")>]
            variables: Dictionary<string, string>,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Serialization method override: xml, html, text, or json.")>]
            outputMethod: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Base URI used to resolve relative doc()/collection() calls inside the query.")>]
            baseUri: string,
            [<Optional; DefaultParameterValue(false)>]
            [<Description("If true, only compile and validate the query; do not execute it.")>]
            checkOnly: bool
        ) : string =
        runXQuery
            query
            (Option.ofObj contextItem)
            (pairsOf variables)
            (Option.ofObj outputMethod)
            (Option.ofObj baseUri)
            checkOnly
        |> serialize

    [<McpServerTool(Name = "saxon_info")>]
    [<Description("Report the Saxon processor product name, version, edition, and the XSLT/XQuery/XPath/XSD spec levels it supports. Call this first if unsure what this server can run.")>]
    static member SaxonInfo() : string =
        JsonSerializer.Serialize(info (), jsonOptions)
