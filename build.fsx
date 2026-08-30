#r "paket:
nuget FSharp.Core 8.0.401
nuget Fake.Core.Target 6.1.4
nuget Fake.DotNet.Cli 6.1.4
nuget Fake.IO.FileSystem 6.1.4 //"

open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators

Target.initEnvironment ()

let project = "src/SaxonMcp/SaxonMcp.fsproj"
let configuration = DotNet.BuildConfiguration.Release
let publishDir = "publish"

Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    ++ publishDir
    |> Shell.cleanDirs
)

Target.create "Restore" (fun _ -> DotNet.restore id project)

Target.create "Build" (fun _ ->
    project
    |> DotNet.build (fun opts ->
        { opts with
            Configuration = configuration
            NoRestore = true })
)

Target.create "Test" (fun _ ->
    // No-op until a *.Tests.fsproj/csproj exists; picked up automatically once one does.
    !! "**/*.Tests.fsproj"
    ++ "**/*.Tests.csproj"
    |> Seq.iter (fun testProject ->
        testProject
        |> DotNet.test (fun opts ->
            { opts with
                Configuration = configuration
                NoBuild = true }))
)

Target.create "Publish" (fun _ ->
    project
    |> DotNet.publish (fun opts ->
        { opts with
            Configuration = configuration
            OutputPath = Some publishDir
            NoRestore = true })
)

open Fake.Core.TargetOperators

"Restore" ==> "Build" ==> "Test" ==> "Publish"

Target.runOrDefaultWithArguments "Build"
