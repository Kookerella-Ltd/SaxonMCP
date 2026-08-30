/// Thin wrapper around Saxon HE's .NET API (Saxon.Api / SaxonCS-HE) for compiling
/// and running XSLT 3.0 stylesheets and XQuery 3.1 queries against in-memory text.
module SaxonMcp.SaxonEngine

open System
open System.Collections.Generic
open System.IO
open Saxon.Api

type Diagnostic =
    { Severity: string
      Message: string
      ErrorCode: string option
      Line: int option
      Column: int option
      ModuleUri: string option }

type RunOutcome =
    { Success: bool
      Output: string option
      Diagnostics: Diagnostic list }

/// Saxon docs recommend creating a single Processor per application; the factory
/// methods it exposes (NewXsltCompiler, NewXQueryCompiler, ...) are safe to call
/// repeatedly and concurrently.
let processor = Processor()

let private positiveOrNone (n: int) = if n > 0 then Some n else None

let private diagFromError (err: Error) : Diagnostic =
    let loc = err.Location
    let hasLoc = not (isNull (box loc))
    { Severity = if err.IsWarning then "warning" else "error"
      Message = err.Message
      ErrorCode = if isNull (box err.ErrorCode) then None else Some (err.ErrorCode.ToString())
      Line = if hasLoc then positiveOrNone loc.LineNumber else None
      Column = if hasLoc then positiveOrNone loc.ColumnNumber else None
      ModuleUri =
        if hasLoc && not (isNull (box loc.SystemId)) then Some (loc.SystemId.ToString()) else None }

let private diagFromException (ex: SaxonApiException) : Diagnostic =
    { Severity = if ex.IsWarning then "warning" else "error"
      Message = ex.Message
      ErrorCode = if isNull (box ex.ErrorCode) then None else Some (ex.ErrorCode.ToString())
      Line = positiveOrNone ex.LineNumber
      Column = positiveOrNone ex.ColumnNumber
      ModuleUri = if isNull ex.ModuleUri then None else Some ex.ModuleUri }

let private diagFromGenericException (ex: exn) : Diagnostic =
    { Severity = "error"; Message = ex.Message; ErrorCode = None; Line = None; Column = None; ModuleUri = None }

let private buildSourceNode (xml: string) : XdmNode =
    use reader = new StringReader(xml)
    processor.NewDocumentBuilder().Build(reader :> TextReader)

let private toXdmValue (s: string) : XdmValue =
    XdmAtomicValue(s) :> XdmValue

let private applyOutputMethod (serializer: Serializer) (outputMethod: string option) =
    outputMethod |> Option.iter (fun m -> serializer.SetOutputProperty(Serializer.METHOD, m))

/// Compile (and, unless checkOnly is set, run) an XSLT 3.0 stylesheet.
///
/// - If `initialTemplate` is given, that named template is invoked (the standard
///   way to run a stylesheet that needs no source document).
/// - Else if `source` is given, templates are applied starting at its document node.
/// - Else the stylesheet's own `xsl:initial-template` (if any) is used.
let runXslt
    (stylesheet: string)
    (source: string option)
    (initialTemplate: string option)
    (parameters: (string * string) list)
    (outputMethod: string option)
    (baseUri: string option)
    (checkOnly: bool)
    : RunOutcome =

    let diagnostics = ResizeArray<Diagnostic>()
    let reporter = ErrorReporter(fun err -> diagnostics.Add(diagFromError err))

    try
        let compiler = processor.NewXsltCompiler()
        compiler.ErrorReporter <- reporter
        baseUri |> Option.iter (fun u -> compiler.BaseUri <- Uri(u))

        use styleReader = new StringReader(stylesheet)
        let executable =
            try
                Some (compiler.Compile(styleReader :> TextReader))
            with :? SaxonApiException as ex ->
                if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
                None

        match executable with
        | None ->
            { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
        | Some _ when checkOnly ->
            { Success = true; Output = None; Diagnostics = List.ofSeq diagnostics }
        | Some exec ->
            let transformer = exec.Load30()
            transformer.ErrorReporter <- reporter

            if not (List.isEmpty parameters) then
                let dict = Dictionary<QName, XdmValue>()
                for (name, value) in parameters do
                    dict.[QName(name)] <- toXdmValue value
                transformer.SetStylesheetParameters(dict)

            let sourceNode = source |> Option.map buildSourceNode
            sourceNode |> Option.iter (fun n -> transformer.GlobalContextItem <- n :> XdmItem)

            use sw = new StringWriter()
            let serializer = processor.NewSerializer(sw)
            applyOutputMethod serializer outputMethod

            try
                match initialTemplate, sourceNode with
                | Some name, _ -> transformer.CallTemplate(QName(name), serializer)
                | None, Some node -> transformer.ApplyTemplates(node :> XdmValue, serializer)
                | None, None -> transformer.CallTemplate((null: QName), serializer)
                serializer.Close()
                { Success = true; Output = Some (sw.ToString()); Diagnostics = List.ofSeq diagnostics }
            with :? SaxonApiException as ex ->
                if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
                { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
    with
    | :? SaxonApiException as ex ->
        if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
        { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
    | ex ->
        diagnostics.Add(diagFromGenericException ex)
        { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }

/// Compile (and, unless checkOnly is set, run) an XQuery 3.1 query.
let runXQuery
    (query: string)
    (contextItem: string option)
    (variables: (string * string) list)
    (outputMethod: string option)
    (baseUri: string option)
    (checkOnly: bool)
    : RunOutcome =

    let diagnostics = ResizeArray<Diagnostic>()
    let reporter = ErrorReporter(fun err -> diagnostics.Add(diagFromError err))

    try
        let compiler = processor.NewXQueryCompiler()
        compiler.ErrorReporter <- reporter
        baseUri |> Option.iter (fun u -> compiler.BaseUri <- Uri(u))

        let executable =
            try
                Some (compiler.Compile(query))
            with :? SaxonApiException as ex ->
                if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
                None

        match executable with
        | None ->
            { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
        | Some _ when checkOnly ->
            { Success = true; Output = None; Diagnostics = List.ofSeq diagnostics }
        | Some exec ->
            let evaluator = exec.Load()
            evaluator.ErrorReporter <- reporter

            contextItem |> Option.iter (fun xml -> evaluator.ContextItem <- (buildSourceNode xml) :> XdmItem)

            for (name, value) in variables do
                evaluator.SetExternalVariable(QName(name), toXdmValue value)

            use sw = new StringWriter()
            let serializer = processor.NewSerializer(sw)
            applyOutputMethod serializer outputMethod

            try
                evaluator.Run(serializer)
                serializer.Close()
                { Success = true; Output = Some (sw.ToString()); Diagnostics = List.ofSeq diagnostics }
            with :? SaxonApiException as ex ->
                if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
                { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
    with
    | :? SaxonApiException as ex ->
        if diagnostics.Count = 0 then diagnostics.Add(diagFromException ex)
        { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }
    | ex ->
        diagnostics.Add(diagFromGenericException ex)
        { Success = false; Output = None; Diagnostics = List.ofSeq diagnostics }

let info () =
    {| product = processor.ProductTitle
       version = processor.ProductVersion
       edition = processor.Edition
       xsltVersion = "3.0"
       xqueryVersion = "3.1"
       xpathVersion = "3.1"
       xsdVersion = "1.1" |}
