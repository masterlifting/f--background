module Core

open System
open System.Threading
open DSL
open Logging

open Domain.Settings

module TaskScheduler =
    open Domain.Core

    let getTaskExpirationToken taskName scheduler =
        async {
            let now = DateTime.UtcNow.AddHours(scheduler.TimeShift |> float)
            let cts = new CancellationTokenSource()

            if not scheduler.IsEnabled then
                $"Task '{taskName}' is disabled" |> Logger.logWarning
                do! cts.CancelAsync() |> Async.AwaitTask

            if not cts.IsCancellationRequested then
                match scheduler.StopWork with
                | Some stopWork ->
                    match stopWork - now with
                    | delay when delay > TimeSpan.Zero ->
                        $"Task '{taskName}' will be stopped at {stopWork}" |> Logger.logWarning
                        cts.CancelAfter delay
                    | _ -> do! cts.CancelAsync() |> Async.AwaitTask
                | _ -> ()

            if not cts.IsCancellationRequested then
                match scheduler.StartWork - now with
                | delay when delay > TimeSpan.Zero ->
                    $"Task '{taskName}' will start at {scheduler.StartWork}" |> Logger.logWarning
                    do! Async.Sleep delay
                | _ -> ()

                if scheduler.IsOnce then
                    $"Task '{taskName}' will be run once" |> Logger.logWarning
                    cts.CancelAfter(scheduler.Delay.Subtract(TimeSpan.FromSeconds 1.0))

            return cts.Token
        }

module TaskHandler =
    open Domain.Core
    open System.IO

    let saveStep (step: TaskStepState) =
        async {
            let file = $"{Environment.CurrentDirectory}/steps/{step.Id}.json"

            if not (Directory.Exists(Path.GetDirectoryName(file))) then
                ignore (Directory.CreateDirectory(Path.GetDirectoryName(file)))

            let state =
                $"{{\"status\":\"{step.Status}\",\"attempts\":{step.Attempts},\"message\":\"{step.Message}\",\"updated_at\":\"{step.UpdatedAt}\"}};"

            do! File.AppendAllLinesAsync(file, [ state ]) |> Async.AwaitTask
        }

    let loadSteps fileName =
        async {
            if File.Exists fileName then
                let! content = File.ReadAllTextAsync fileName |> Async.AwaitTask
                return content.Split(';')
            else
                return [||]
        }

    let private handleSteps taskName steps stepHandlers (ct: CancellationToken) =

        let handleStep (step: TaskStep) (stepHandler: TaskStepHandler) =
            async {
                if ct.IsCancellationRequested then
                    ct.ThrowIfCancellationRequested()

                if stepHandler.Name <> step.Name then
                    $"Task '{taskName}'. Step '{step.Name}'. Handler '{stepHandler.Name}' does not match"
                    |> Logger.logError
                else
                    $"Task '{taskName}'. Step '{step.Name}'. Started" |> Logger.logTrace

                    let! previousSteps = loadSteps $"{Environment.CurrentDirectory}/steps/{taskName}_{step.Name}.json"

                    match! stepHandler.Handle() with
                    | Ok msg ->
                        let state =
                            { Id = $"{taskName}_{step.Name}"
                              Status = Completed
                              Attempts = previousSteps.Length + 1
                              Message = msg
                              UpdatedAt = DateTime.UtcNow }

                        do! saveStep state
                        $"Task '{taskName}'. Step '{step.Name}'. Completed" |> Logger.logInfo

                    | Error error ->
                        let state =
                            { Id = $"{taskName}_{step.Name}"
                              Status = Failed
                              Attempts = previousSteps.Length + 1
                              Message = error
                              UpdatedAt = DateTime.UtcNow }

                        do! saveStep state
                        $"Task '{taskName}'. Step '{step.Name}'. Failed. {error}" |> Logger.logError
            }

        let rec innerLoop (steps: TaskStep list) (stepHandlers: TaskStepHandler list) =
            async {
                match steps, stepHandlers with
                | [], _ -> ()
                | step :: stepsTail, [] ->
                    $"Task '{taskName}'. Step '{step.Name}'. Handler was not found"
                    |> Logger.logError

                    return! innerLoop step.Steps []
                    return! innerLoop stepsTail []
                | step :: stepsTail, stepHandler :: stepHandlerTail ->
                    do! handleStep step stepHandler
                    return! innerLoop step.Steps stepHandler.Steps
                    return! innerLoop stepsTail stepHandlerTail
            }

        innerLoop steps stepHandlers

    let internal startTask (task: Task) (taskHandlers: TaskHandler list) workerCt =
        async {
            match taskHandlers |> Seq.tryFind (fun x -> x.Name = task.Name) with
            | None -> $"Task '{task.Name}'. Handler was not found" |> Logger.logError
            | Some taskHandler ->
                let! taskCt = TaskScheduler.getTaskExpirationToken task.Name task.Scheduler

                let rec innerLoop () =
                    async {
                        match taskCt.IsCancellationRequested with
                        | true -> $"Task '{task.Name}'. Stopped" |> Logger.logWarning
                        | false ->
                            $"Task '{task.Name}'. Started" |> Logger.logDebug
                            do! handleSteps task.Name task.Steps taskHandler.Steps workerCt
                            $"Task '{task.Name}'. Completed" |> Logger.logDebug

                            $"Task '{task.Name}'. Next run will be in {task.Scheduler.Delay}"
                            |> Logger.logTrace

                            do! Async.Sleep task.Scheduler.Delay
                            do! innerLoop ()
                    }

                return! innerLoop ()
        }

let startWorker duration handlers =
    try
        $"The worker will be running for {duration} seconds" |> Logger.logWarning
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds duration)

        match Configuration.getSection<Domain.Settings.Section> "Worker" with
        | Some settings ->
            settings.Tasks
            |> Seq.map (fun taskSettings -> Domain.Core.toTask taskSettings.Key taskSettings.Value)
            |> Seq.map (fun task -> TaskHandler.startTask task handlers cts.Token)
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore
        | None -> failwith "Worker settings was not found"
    with
    | :? OperationCanceledException -> $"The worker has been cancelled" |> Logger.logWarning
    | ex -> ex.Message |> Logger.logError
