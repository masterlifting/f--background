module Worker.Mapper

open System
open Infrastructure
open Infrastructure.DSL.ActivePatterns
open Infrastructure.Domain.Graph
open Infrastructure.DSL.Graph
open Domain.Core

let private defaultWorkdays =
    set
        [ DayOfWeek.Monday
          DayOfWeek.Tuesday
          DayOfWeek.Wednesday
          DayOfWeek.Thursday
          DayOfWeek.Friday
          DayOfWeek.Saturday
          DayOfWeek.Sunday ]

let private parseWorkdays (workdays: string) =
    match workdays with
    | IsString str ->
        match str.Split(",") with
        | data ->
            data
            |> Array.map (function
                | "mon" -> Ok DayOfWeek.Monday
                | "tue" -> Ok DayOfWeek.Tuesday
                | "wed" -> Ok DayOfWeek.Wednesday
                | "thu" -> Ok DayOfWeek.Thursday
                | "fri" -> Ok DayOfWeek.Friday
                | "sat" -> Ok DayOfWeek.Saturday
                | "sun" -> Ok DayOfWeek.Sunday
                | _ -> Error "Workday is not valid. Expected values: 'mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'.")
            |> DSL.Seq.resultOrError
            |> Result.map Set.ofList
    | _ -> Ok defaultWorkdays

let private parseTimeSpan (value: string) =
    match value with
    | IsString str ->
        match str with
        | IsTimeSpan value -> Ok <| Some value
        | _ -> Error "Time value is not valid. Expected format: 'dd.hh:mm:ss'."
    | _ -> Ok None

let private parseLimit (limit: int) =
    if limit <= 0 then None else Some <| uint limit

let private mapSchedule (schedule: Domain.Persistence.Schedule) =
    match schedule.IsEnabled with
    | false -> Ok None
    | true ->
        schedule.Workdays
        |> parseWorkdays
        |> Result.bind (fun workdays ->
            schedule.Delay
            |> parseTimeSpan
            |> Result.map (fun delay ->
                Some
                    { StartWork = Option.ofNullable schedule.StartWork |> Option.defaultValue DateTime.UtcNow
                      StopWork = Option.ofNullable schedule.StopWork
                      Workdays = workdays
                      Delay = delay
                      Limit = schedule.Limit |> parseLimit
                      TimeShift = schedule.TimeShift }))

let private mapTask (task: Domain.Persistence.Task) (handle: HandleTask) =
    task.Schedule
    |> mapSchedule
    |> Result.bind (fun schedule ->
        task.Duration
        |> parseTimeSpan
        |> Result.map (fun duration ->
            { Name = task.Name
              Parallel = task.Parallel
              Recursively = task.Recursively
              Duration = duration
              Schedule = schedule
              Handle = handle }))

let buildCoreGraph (task: Domain.Persistence.Task) handlersGraph =
    let getHandle nodeName graph =
        match findNode nodeName graph with
        | Some handler -> handler.Value.Handle
        | None -> None

    let createNode nodeName (task: Domain.Persistence.Task) innerLoop =
        let taskName = nodeName |> buildNodeName <| task.Name

        innerLoop (Some taskName) task.Steps
        |> Result.bind (fun steps ->
            let handle = getHandle taskName handlersGraph

            mapTask task handle
            |> Result.map (fun task -> Node(task, steps)))

    let rec innerLoop nodeName (tasks: Domain.Persistence.Task array) =
        match tasks with
        | [||] -> Ok []
        | null -> Ok []
        | _ ->
            tasks
            |> Array.map (fun task -> createNode nodeName task innerLoop)
            |> DSL.Seq.resultOrError

    createNode None task innerLoop
