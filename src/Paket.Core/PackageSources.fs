﻿module Paket.PackageSources

open System
open System.IO
open System.Text.RegularExpressions

open Paket.Logging
open Chessie.ErrorHandling

open Newtonsoft.Json

type EnvironmentVariable = 
    { Variable : string
      Value    : string }

    static member Create(variable) = 
        let envVarRegex = Regex("^%(\w*)%$")
        if envVarRegex.IsMatch(variable) then
            let trimmed = envVarRegex.Match(variable).Groups.[1].Value
            let expanded = Environment.GetEnvironmentVariable(trimmed)
            if expanded = null then 
                traceWarnfn "environment variable '%s' not found" variable
                Some { Variable = variable; Value = ""}
            else 
                Some { Variable = variable; Value = expanded }
        else
            None

type NugetSourceAuthentication = 
    | PlainTextAuthentication of username : string * password : string
    | EnvVarAuthentication of usernameVar : EnvironmentVariable * passwordVar : EnvironmentVariable
    | ConfigAuthentication of username : string * password : string

let toBasicAuth = function
    | PlainTextAuthentication(username,password) | ConfigAuthentication(username, password) ->
        Credentials(username, password)
    | EnvVarAuthentication(usernameVar, passwordVar) -> 
        Credentials(usernameVar.Value, passwordVar.Value)

let tryParseWindowsStyleNetworkPath (path : string) =
    let trimmed = path.TrimStart()
    match Environment.OSVersion.Platform with
        | PlatformID.Unix | PlatformID.MacOSX when trimmed.StartsWith @"\\" ->
            trimmed.Replace('\\', '/') |> sprintf "smb:%s" |> Some
        | _  -> None

type NugetSource = 
    { Url : string
      Authentication : NugetSourceAuthentication option }

type NugetV3SourceResourceJSON =
    { [<JsonProperty("@type")>]
      Type : string;
      [<JsonProperty("@id")>]
      ID: string }
type NugetV3SourceRootJSON =
    { [<JsonProperty("resources")>]
      Resources : NugetV3SourceResourceJSON [] }
type NugetV3Source = 
    { Url : string
      Authentication : NugetSourceAuthentication option }

type NugetV3ResourceType =
| AutoComplete
| Registration
with
    member this.AsString = 
        match this with
        | AutoComplete -> "SearchAutoCompleteService"
        | Registration -> "RegistrationsBaseUrl"

let mutable private nugetV3Resources : Map<NugetV3Source * NugetV3ResourceType, string> = Map.empty 
        
let getNugetV3Resource (source : NugetV3Source) (resourceType : NugetV3ResourceType) =
    async {
        let key = (source, resourceType)
        return!
            match nugetV3Resources |> Map.tryFind key with
            | Some x -> async { return x }
            | None -> 
                async {
                    let basicAuth = source.Authentication |> Option.map toBasicAuth
                    let! rawData = safeGetFromUrl(basicAuth, source.Url, acceptJson)
                    let rawData =
                        match rawData with
                        | None -> failwithf "couldnt load resources from %s" source.Url
                        | Some x -> x
                    let json = JsonConvert.DeserializeObject<NugetV3SourceRootJSON>(rawData)
                    let resources = 
                        json.Resources 
                        |> Seq.distinctBy(fun x -> x.Type.ToLower())
                        |> Seq.map(fun x -> x.Type.ToLower(), x.ID) 

                    let newMap = 
                        lock nugetV3Resources (fun() ->
                            let newMap =
                                resources
                                |> Seq.fold(fun m (res, value) ->
                                    let resType =
                                        match res.ToLower() with
                                        | "searchautocompleteservice" -> Some AutoComplete
                                        | "registrationsbaseurl" -> Some Registration
                                        | _ -> None
                                    match resType with
                                    | None -> m
                                    | Some resType ->
                                        m |> Map.add (source, resType) value
                                ) nugetV3Resources
                            
                            nugetV3Resources <- newMap
                        
                            newMap)

                    return
                        match newMap |> Map.tryFind key with
                        | Some x -> x
                        | None ->
                            failwithf "could not find an %s endpoint for %s" (resourceType.ToString()) source.Url
                }
                
    }
let userNameRegex = Regex("username[:][ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
let passwordRegex = Regex("password[:][ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let private parseAuth(text:string, source) =
    let getAuth() = ConfigFile.GetAuthentication source |> Option.map (function Credentials(username, password) -> ConfigAuthentication(username, password) | _ -> ConfigAuthentication("",""))
    if text.Contains("username:") || text.Contains("password:") then
        if not (userNameRegex.IsMatch(text) && passwordRegex.IsMatch(text)) then 
            failwithf "Could not parse auth in \"%s\"" text

        let username = userNameRegex.Match(text).Groups.[1].Value
        let password = passwordRegex.Match(text).Groups.[1].Value

        let auth = 
            match EnvironmentVariable.Create(username),
                  EnvironmentVariable.Create(password) with
            | Some userNameVar, Some passwordVar ->
                EnvVarAuthentication(userNameVar, passwordVar) 
            | _, _ -> 
                PlainTextAuthentication(username, password)

        match toBasicAuth auth with
        | Credentials(username, password) when username = "" && password = "" -> getAuth()
        | _ -> Some auth
    else
        getAuth()

/// Represents the package source type.
type PackageSource =
| Nuget of NugetSource
| NugetV3 of NugetV3Source
| LocalNuget of string
    override this.ToString() =
        match this with
        | Nuget source -> source.Url
        | NugetV3 source -> source.Url
        | LocalNuget path -> path

    static member Parse(line : string) =
        let sourceRegex = Regex("source[ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase)
        let parts = line.Split ' '
        let source = 
            if sourceRegex.IsMatch line then
                sourceRegex.Match(line).Groups.[1].Value.TrimEnd([| '/' |])
            else
                parts.[1].Replace("\"","").TrimEnd([| '/' |])

        let feed = 
            match source.TrimEnd([|'/'|]) with
            | "https://api.nuget.org/v3/index.json" -> Constants.DefaultNugetV3Stream 
            | "https://www.nuget.org/api/v2" -> Constants.DefaultNugetStream
            | _ -> source

        PackageSource.Parse(feed, parseAuth(line, feed))

    static member Parse(source,auth) = 
        match tryParseWindowsStyleNetworkPath source with
        | Some path -> PackageSource.Parse(path)
        | _ ->
            match System.Uri.TryCreate(source, System.UriKind.Absolute) with
            | true, uri -> 
                if uri.Scheme = System.Uri.UriSchemeFile then 
                    LocalNuget(source) 
                else 
                    if source.ToLower().EndsWith("v3/index.json") then
                        NugetV3({ Url = source; Authentication = auth })
                    else
                        Nuget({ Url = source; Authentication = auth })
            | _ ->  match System.Uri.TryCreate(source, System.UriKind.Relative) with
                    | true, uri -> LocalNuget(source)
                    | _ -> failwithf "unable to parse package source: %s" source

    member this.Url = 
        match this with
        | Nuget n -> n.Url
        | NugetV3 n -> n.Url
        | LocalNuget n -> n

    member this.Auth = 
        match this with
        | Nuget n -> n.Authentication
        | NugetV3 n -> n.Authentication
        | LocalNuget n -> None

    static member NugetSource url = Nuget { Url = url; Authentication = None }

    static member WarnIfNoConnection (source,_) = 
        let n url auth =
            use client = Utils.createWebClient(url, auth |> Option.map toBasicAuth)
            try client.DownloadData url |> ignore 
            with _ ->
                traceWarnfn "Unable to ping remote Nuget feed: %s." url
        match source with
        | Nuget x -> n x.Url x.Authentication
        | NugetV3 x -> n x.Url x.Authentication
        | LocalNuget path -> 
            if not <| File.Exists path then 
                traceWarnfn "Local Nuget feed doesn't exist: %s." path

let DefaultNugetSource = PackageSource.NugetSource Constants.DefaultNugetStream