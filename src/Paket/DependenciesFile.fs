namespace Paket

open System
open System.IO
open Paket

/// [omit]
module DependenciesFileParser = 

    let parseVersionRange (text : string) : VersionRange = 
        let splitVersion (text:string) =
            let tokens = ["~>";">=";"=" ]
            match tokens |> List.tryFind(text.StartsWith) with
            | Some token -> token, text.Replace(token + " ", "")
            | None -> "=", text

        try
            match splitVersion text with
            | ">=", version -> VersionRange.AtLeast(version)
            | "~>", minimum ->
                let maximum =                    
                    let promote index (values:string array) =
                        let parsed, number = Int32.TryParse values.[index]
                        if parsed then values.[index] <- (number + 1).ToString()
                        if values.Length > 1 then values.[values.Length - 1] <- "0"
                        values

                    let parts = minimum.Split '.'
                    let penultimateItem = Math.Max(parts.Length - 2, 0)
                    let promoted = parts |> promote penultimateItem
                    String.Join(".", promoted)
                VersionRange.Between(minimum, maximum)
            | _, version -> VersionRange.Exactly(version)
        with
        | _ -> failwithf "could not parse version range \"%s\"" text

    let private (|Remote|Package|Blank|ReferencesMode|SourceFile|) (line:string) =
        match line.Trim() with
        | _ when String.IsNullOrWhiteSpace line -> Blank
        | trimmed when trimmed.StartsWith "source" -> 
            let fst = trimmed.IndexOf("\"")
            let snd = trimmed.IndexOf("\"",fst+1)
            Remote (trimmed.Substring(fst,snd-fst).Replace("\"",""))                
        | trimmed when trimmed.StartsWith "nuget" -> Package(trimmed.Replace("nuget","").Trim())
        | trimmed when trimmed.StartsWith "references" -> ReferencesMode(trimmed.Replace("references","").Trim() = "strict")
        | trimmed when trimmed.StartsWith "github" ->
            let parts = trimmed.Replace("\"", "").Split ' '
            let getParts (projectSpec:string) =
                match projectSpec.Split [|':'; '/'|] with
                | [| owner; project |] -> owner, project, None
                | [| owner; project; commit |] -> owner, project, Some commit
                | _ -> failwithf "invalid github specification:%s     %s" Environment.NewLine trimmed
            match parts with
            | [| _; projectSpec; fileSpec |] -> SourceFile(getParts projectSpec, fileSpec)
            | _ -> failwithf "invalid github specification:%s     %s" Environment.NewLine trimmed
        | _ -> Blank
    
    let parseDependenciesFile (lines:string seq) = 
        ((0, false, [], [], []), lines)
        ||> Seq.fold(fun (lineNo, referencesMode, sources: PackageSource list, packages, sourceFiles) line ->
            let lineNo = lineNo + 1
            try
                match line with
                | Remote newSource -> lineNo, referencesMode, (PackageSource.Parse(newSource.TrimEnd([|'/'|])) :: sources), packages, sourceFiles
                | Blank -> lineNo, referencesMode, sources, packages, sourceFiles
                | ReferencesMode mode -> lineNo, mode, sources, packages, sourceFiles
                | Package details ->
                    let parts = details.Split('"')
                    if parts.Length < 4 || String.IsNullOrWhiteSpace parts.[1] || String.IsNullOrWhiteSpace parts.[3] then
                        failwith "missing \""
                    let version = parts.[3]
                    lineNo, referencesMode, sources, 
                        { Sources = sources
                          Name = parts.[1]
                          ResolverStrategy = if version.StartsWith "!" then ResolverStrategy.Min else ResolverStrategy.Max
                          VersionRange = parseVersionRange(version.Trim '!') } :: packages, sourceFiles
                | SourceFile((owner,project, commit), path) ->
                    let newSourceFile = { Owner = owner
                                          Project = project
                                          Commit = commit
                                          Name = path }
                    tracefn "  %O" newSourceFile
                    lineNo, referencesMode, sources, packages, newSourceFile :: sourceFiles
                    
            with
            | exn -> failwithf "Error in paket.dependencies line %d%s  %s" lineNo Environment.NewLine exn.Message)
        |> fun (_,mode,_,packages,remoteFiles) ->
            mode,
            packages |> List.rev,
            remoteFiles |> List.rev

/// Allows to parse and analyze paket.dependencies files.
type DependenciesFile(strictMode,packages : UnresolvedPackage list, remoteFiles : SourceFile list) = 
    let packages = packages |> Seq.toList
    let dependencyMap = Map.ofSeq (packages |> Seq.map (fun p -> p.Name, p.VersionRange))
    member __.DirectDependencies = dependencyMap
    member __.Packages = packages
    member __.RemoteFiles = remoteFiles
    member __.Strict = strictMode
    member __.Resolve(force, discovery : IDiscovery) = PackageResolver.Resolve(force, discovery, packages)
    static member FromCode(code:string) : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile <| code.Replace("\r\n","\n").Replace("\r","\n").Split('\n'))
    static member ReadFromFile fileName : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile <| File.ReadAllLines fileName)