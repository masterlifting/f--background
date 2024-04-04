module Persistence

open System.IO
open System
open System.Text.Json

module private File =
    open Domain.Core

    let saveStep taskName stepState =
        async {
            let path = $"{Environment.CurrentDirectory}/tasks"
            let file = $"{path}/{taskName}.json"

            if not (Directory.Exists(path)) then
                Directory.CreateDirectory(path) |> ignore

            let state: Domain.Persistence.StepState =
                { Id = stepState.Id
                  Status =
                    match stepState.Status with
                    | Pending -> "Pending"
                    | Running -> "Running"
                    | Completed -> "Completed"
                    | Failed -> "Failed"
                  Attempts = stepState.Attempts
                  Message = stepState.Message
                  UpdatedAt = stepState.UpdatedAt }

            do!
                File.AppendAllLinesAsync(file, [ JsonSerializer.Serialize state ])
                |> Async.AwaitTask
        }

    let getLastStep taskName =
        async {
            let path = $"{Environment.CurrentDirectory}/tasks"
            let file = $"{path}/{taskName}.json"

            if File.Exists file then
                let! content = File.ReadAllLinesAsync file |> Async.AwaitTask

                return
                    content
                    |> Seq.tryLast
                    |> Option.map (fun x -> JsonSerializer.Deserialize<Domain.Persistence.StepState>(x))
                    |> Option.map (fun x ->
                        { Id = x.Id
                          Status =
                            match x.Status with
                            | "Pending" -> Pending
                            | "Running" -> Running
                            | "Completed" -> Completed
                            | "Failed" -> Failed
                            | _ -> Failed
                          Attempts = x.Attempts
                          Message = x.Message
                          UpdatedAt = x.UpdatedAt })
            else
                return None
        }

module private Settings =
    let private getTasks () =
        match Configuration.getSection<Domain.Settings.Section> "Worker" with
        | Some settings ->
            settings.Tasks
            |> Seq.map (fun taskSettings -> Domain.Core.toTask taskSettings.Key taskSettings.Value)
            |> Ok
        | None -> Error "Worker settings wasnot found"

    let getTaskNames () =
        match getTasks () with
        | Ok tasks -> tasks |> Seq.map (fun x -> x.Name) |> Ok
        | Error error -> Error error

    let getTask name =
        match getTasks () with
        | Ok tasks ->
            let task = tasks |> Seq.tryFind (fun x -> x.Name = name)

            match task with
            | Some x -> Ok x
            | None -> Error "Task was not found"
        | Error error -> Error error

let setStepState = File.saveStep
let getLastStepState = File.getLastStep
let getConfiguredTaskNames = Settings.getTaskNames
let getTask name = async { return Settings.getTask name }
