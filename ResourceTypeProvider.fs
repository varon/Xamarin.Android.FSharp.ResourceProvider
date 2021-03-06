﻿namespace Xamarin.Android.FSharp

open System
open System.IO
open System.Reflection
open System.CodeDom.Compiler
open System.Xml.Linq
open FSharp.Core.CompilerServices
open Microsoft.CSharp
open Microsoft.FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes

[<TypeProvider>]
type ResourceProvider(config : TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()

    let ctxt = ProvidedTypesContext.Create(config)

    let compiler = new CSharpCodeProvider()
    let (/) a b = Path.Combine(a,b)
    let pathToResources = config.ResolutionFolder/"Resources"
    let resourceFileName = pathToResources/"Resource.designer.cs"
    // watcher doesn't trigger when I specify the filename exactly
    let watcher = new FileSystemWatcher(pathToResources, "*.cs", EnableRaisingEvents=true)

    let asm = sprintf "ProvidedTypes%s.dll" (Guid.NewGuid() |> string)
    let outputPath = config.TemporaryFolder/asm
    let generate sourceCode =
        let cp = CompilerParameters(
                    GenerateInMemory = false,
                    OutputAssembly = outputPath,
                    TempFiles = new TempFileCollection(config.TemporaryFolder, false),
                    CompilerOptions = "/nostdlib /noconfig")

        let addRef ref = 
            cp.ReferencedAssemblies.Add ref |> ignore

        let addProjectReferences() =
            // This might add references that we don't need. Not sure it matters.
            let parentFolder = (Directory.GetParent config.ResolutionFolder)
            let packages = sprintf "%c%s%c" Path.DirectorySeparatorChar "packages" Path.DirectorySeparatorChar
            config.ReferencedAssemblies
            |> Array.filter(fun r -> File.Exists r && (r.StartsWith parentFolder.FullName || r.IndexOf(packages, StringComparison.OrdinalIgnoreCase) >= 0))
            |> Array.iter addRef

        let addReference assemblyFileName =
            printfn "Adding reference %s" assemblyFileName
            let reference =
                config.ReferencedAssemblies |> Array.tryFind(fun r -> r.EndsWith(sprintf "%c%s" Path.DirectorySeparatorChar assemblyFileName, StringComparison.InvariantCultureIgnoreCase)
                                                                      && r.IndexOf("Facade") = -1)

            match reference with
            | Some ref -> addRef ref
                          (Some ref, assemblyFileName)
            | None -> printfn "Did not find %s in referenced assemblies." assemblyFileName
                      None, assemblyFileName


        printfn "F# Android resource provider"
        let system = addReference "System.dll"
        let mscorlib = addReference "mscorlib.dll"
        let android = addReference "Mono.Android.dll"
        let nunitLite = addReference "Xamarin.Android.NUnitLite.dll"

        addProjectReferences()

        let addIfMissingReference addResult =
            match android, addResult with
            | (Some androidRef, _), (None, assemblyFileName) ->
                // When the TP is ran from XS, config.ReferencedAssemblies doesn't contain mscorlib or System.dll
                // but from xbuild, it does. Need to investigate why.
                let systemPath = Path.GetDirectoryName androidRef
                addRef (systemPath/".."/"v1.0"/assemblyFileName)
            | _, _ -> ()

        addIfMissingReference system
        addIfMissingReference mscorlib
        addIfMissingReference nunitLite

        let result = compiler.CompileAssemblyFromSource(cp, [| sourceCode |])
        if result.Errors.HasErrors then
            let errors = [ for e in result.Errors do yield e ] 
                         |> List.filter (fun e -> not e.IsWarning )

            if errors.Length > 0 then
                printfn "%A" errors
                failwithf "%A" errors

        let asm = Assembly.ReflectionOnlyLoadFrom cp.OutputAssembly
        let resourceType = asm.GetTypes() |> Array.tryFind(fun t -> t.Name = "Resource")
        match resourceType with
        | Some typ ->
            let csharpAssembly = Assembly.GetExecutingAssembly()
            let providedAssembly = ProvidedAssembly(ctxt)
            let providedType = ctxt.ProvidedTypeDefinition(csharpAssembly, typ.Namespace, typ.Name, Some typeof<obj>, true, true, false)
            let generatedAssembly = ProvidedAssembly.RegisterGenerated(ctxt, outputPath)
            providedType.AddMembers (typ.GetNestedTypes() |> List.ofArray)
            providedAssembly.AddTypes [providedType]
            this.AddNamespace(typ.Namespace, [providedType])    
        | None -> failwith "No resource type found"
    let invalidate _ =
        printfn "Invalidating resources"
        this.Invalidate()

    do
        printfn "Resource folder %s" config.ResolutionFolder
        printfn "Resource file name %s" resourceFileName
        watcher.Changed.Add invalidate
        watcher.Created.Add invalidate

        AppDomain.CurrentDomain.add_ReflectionOnlyAssemblyResolve(fun _ args ->
            let name = AssemblyName(args.Name)
            printfn "Resolving %s" args.Name
            let existingAssembly = 
                AppDomain.CurrentDomain.GetAssemblies()
                |> Seq.tryFind(fun a -> AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))
            let asm = 
                match existingAssembly with
                | Some a -> printfn "Resolved to %s" a.Location
                            Assembly.ReflectionOnlyLoadFrom a.Location

                | None -> null
            asm)

        let getRootNamespace() =
            // Try and guess what the namespace should be...
            // This will work 99%+ of the time and if it
            // doesn't, a build will fix. This is only until the real
            // resources file has been generated.
            let dir = new DirectoryInfo(config.ResolutionFolder)
            try
                let fsproj = Directory.GetFiles(config.ResolutionFolder, "*.fsproj") |> Array.head
                let nsuri = "http://schemas.microsoft.com/developer/msbuild/2003"
                let ns = XNamespace.Get nsuri
                let doc = XDocument.Load fsproj
                let rootnamespaceNode = doc.Descendants(ns + "RootNamespace") |> Seq.head
                rootnamespaceNode.Value
            with
            | ex -> dir.Name

        let source =
            if File.Exists resourceFileName then
                File.ReadAllText resourceFileName
            else
                let asm = Assembly.GetExecutingAssembly()
                let resourceNames = asm.GetManifestResourceNames()
                use stream = asm.GetManifestResourceStream("Resource.designer.cs")
                use reader = new StreamReader(stream)
                let source = reader.ReadToEnd()

                let namespc = getRootNamespace()
                source.Replace("${Namespace}", namespc)
        generate source

[<assembly: TypeProviderAssembly>]
do()