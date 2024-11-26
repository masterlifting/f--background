[<RequireQualifiedAccess>]
module Worker.Graph

open System
open Infrastructure
open Worker.Domain

let private parseWorkdays workdays =
    match workdays with
    | AP.IsString str ->
        str.Split ','
        |> Array.map (function
            | "mon" -> Ok DayOfWeek.Monday
            | "tue" -> Ok DayOfWeek.Tuesday
            | "wed" -> Ok DayOfWeek.Wednesday
            | "thu" -> Ok DayOfWeek.Thursday
            | "fri" -> Ok DayOfWeek.Friday
            | "sat" -> Ok DayOfWeek.Saturday
            | "sun" -> Ok DayOfWeek.Sunday
            | _ ->
                Error
                <| NotSupported "Workday. Expected values: 'mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'.")
        |> Result.choose
        |> Result.map Set.ofList
    | _ ->
        Ok
        <| set
            [ DayOfWeek.Monday
              DayOfWeek.Tuesday
              DayOfWeek.Wednesday
              DayOfWeek.Thursday
              DayOfWeek.Friday
              DayOfWeek.Saturday
              DayOfWeek.Sunday ]

let private parseDateOnly day =
    match day with
    | AP.IsDateOnly value -> Ok value
    | _ -> Error <| NotSupported "DateOnly. Expected format: 'yyyy-MM-dd'."

let private parseTimeOnly time =
    match time with
    | AP.IsTimeOnly value -> Ok value
    | _ -> Error <| NotSupported "TimeOnly. Expected format: 'hh:mm:ss'."

let private scheduleResult = ResultBuilder()

let private mapSchedule (schedule: External.Schedule) =
    scheduleResult {
        let! workdays = schedule.Workdays |> parseWorkdays
        let! startDate = schedule.StartDate |> Option.toResult parseDateOnly
        let! stopDate = schedule.StopDate |> Option.toResult parseDateOnly
        let! startTime = schedule.StartTime |> Option.toResult parseTimeOnly
        let! stopTime = schedule.StopTime |> Option.toResult parseTimeOnly

        return
            { StartDate = startDate
              StopDate = stopDate
              StartTime = startTime
              StopTime = stopTime
              Workdays = workdays
              TimeZone = schedule.TimeZone }
    }

let private parseTimeSpan timeSpan =
    match timeSpan with
    | AP.IsTimeSpan value -> Ok value
    | _ -> Error <| NotSupported "TimeSpan. Expected format: 'dd.hh:mm:ss'."

let private validateHandler taskName taskEnabled (handler: WorkerTaskHandler option) =
    match taskEnabled, handler with
    | true, None -> Error <| NotFound $"Handler for task '%s{taskName}'."
    | true, Some handler -> Ok <| Some handler
    | false, _ -> Ok None

let private toWorkerTask handler (task: External.TaskGraph) =
    scheduleResult {
        let! recursively = task.Recursively |> Option.toResult parseTimeSpan
        let! duration = task.Duration |> Option.toResult parseTimeSpan
        let! schedule = task.Schedule |> Option.toResult mapSchedule

        return
            { Id = task.Id |> Graph.NodeIdValue
              Name = task.Name
              Parallel = task.Parallel
              Recursively = recursively
              Duration = duration |> Option.defaultValue (TimeSpan.FromMinutes 5.)
              Wait = task.Wait
              Schedule = schedule
              Handler = handler }
    }

let rec createTaskGraph (node: External.TaskGraph) =
    let children =
        match node.Tasks with
        | [||]
        | null -> []
        | _ -> node.Tasks |> Array.toList |> List.map createTaskGraph

    Graph.Node(node, children)

let merge handlersGraph (taskGraph: Graph.Node<External.TaskGraph>) =

    let rec mergeLoop (handler: Graph.Node<WorkerHandler>) =
        match taskGraph |> Graph.BFS.tryFindByName handler.FullName with
        | None -> handler.FullName |> NotFound |> Error
        | Some taskGraphNode ->
            taskGraphNode.Value
            |> toWorkerTask handler.Value.Task
            |> Result.bind (fun workerTask ->
                handler.Children
                |> List.map mergeLoop
                |> Result.choose
                |> Result.map (fun children -> Graph.Node(workerTask, children)))

    handlersGraph |> mergeLoop
