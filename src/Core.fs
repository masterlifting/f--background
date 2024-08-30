module Worker.Core

open System
open System.Threading
open Infrastructure
open Infrastructure.Logging
open Worker.Domain

let rec private handleNode count schedule (deps: HandleNodeDeps) =
    async {
        let nodeName = deps.NodeName

        match! deps.getNode nodeName with
        | Error error -> $"Task %i{count}.'%s{nodeName}'. Failed -> %s{error.Message}" |> Log.error
        | Ok node ->
            let task = { node.Value with Name = nodeName }

            let! schedule = task |> deps.handleNode count schedule
            do! node.Children |> handleNodes count deps schedule

            match task.Recursively with
            | Some _ ->
                let count = count + 1u
                do! handleNode count schedule deps
            | _ -> ()
    }

and handleNodes count deps schedule nodes =
    async {
        if nodes.Length > 0 then

            let nodeName = deps.NodeName

            let nodeHandlers, skipLength =

                let parallelNodes = nodes |> List.takeWhile (_.Value.Parallel)

                match parallelNodes with
                | parallelNodes when parallelNodes.Length < 2 ->

                    let sequentialNodes =
                        nodes |> List.skip 1 |> List.takeWhile (_.Value.Parallel >> not)

                    let tasks =
                        [ nodes[0] ] @ sequentialNodes
                        |> List.map (fun task ->
                            let nodeName = Some nodeName |> Graph.buildNodeName <| task.Value.Name
                            { deps with NodeName = nodeName } |> handleNode count schedule)
                        |> Async.Sequential

                    (tasks, sequentialNodes.Length + 1)

                | parallelNodes ->

                    let tasks =
                        parallelNodes
                        |> List.map (fun task ->
                            let nodeName = Some nodeName |> Graph.buildNodeName <| task.Value.Name
                            { deps with NodeName = nodeName } |> handleNode count schedule)
                        |> Async.Parallel

                    (tasks, parallelNodes.Length)

            do! nodeHandlers |> Async.Ignore

            do! nodes |> List.skip skipLength |> handleNodes count deps schedule
    }

let private runTask deps taskName =
    async {
        $"%s{taskName} Started." |> Log.debug

        use cts =
            match deps.Duration with
            | Some duration -> new CancellationTokenSource(duration)
            | None -> new CancellationTokenSource()

        let run () =
            deps.taskHandler (deps.Configuration, deps.Schedule, cts.Token)

        match! run () with
        | Error error -> $"%s{taskName} Failed -> %s{error.Message}" |> Log.error
        | Ok result ->
            let message = $"%s{taskName} Completed. "

            match result with
            | Success result -> $"%s{message}%A{result}" |> Log.success
            | Warn msg -> $"%s{message}%s{msg}" |> Log.warning
            | Debug msg -> $"%s{message}%s{msg}" |> Log.debug
            | Info msg -> $"%s{message}%s{msg}" |> Log.info
            | Trace msg -> $"%s{message}%s{msg}" |> Log.trace
    }

let private run task configuration taskName =
    async {
        match task.Handler with
        | None -> $"%s{taskName} Skipped." |> Log.trace
        | Some handler ->
            let runTask =
                taskName
                |> runTask
                    { Configuration = configuration
                      Duration = task.Duration
                      Schedule = task.Schedule
                      taskHandler = handler }

            match task.Wait with
            | true -> do! runTask
            | false -> runTask |> Async.Start

        match task.Recursively with
        | Some delay ->
            $"%s{taskName} Next task will be run in {fromTimeSpan delay}." |> Log.trace
            do! Async.Sleep delay
        | None -> ()
    }

let rec private handleTask configuration =
    fun count parentSchedule (task: Task) ->
        async {
            let taskName = $"Task '%i{count}.%s{task.Name}'."

            match! Scheduler.set parentSchedule task.Schedule task.Recursively with
            | Expired(reason, schedule) ->
                $"%s{taskName} Stopped -> %s{reason.Message}" |> Log.warning
                return schedule
            | ExpiredAfter(stopDateTime, schedule) ->
                $"%s{taskName} Stopped after %s{fromDateTime stopDateTime}." |> Log.warning
                return schedule
            | ReadyAfter(delay, schedule) ->
                $"%s{taskName} Will start in %s{fromTimeSpan delay}." |> Log.warning
                do! Async.Sleep delay
                do! taskName |> run task configuration
                return schedule
            | Ready schedule ->
                do! taskName |> run task configuration
                return schedule
        }

let private processGraph nodeName deps =
    handleNode
        1u
        None
        { NodeName = nodeName
          getNode = deps.getTask
          handleNode = handleTask <| deps.Configuration }

let start deps name =
    async {
        try
            let workerName = $"Worker '%s{name}'."

            match! processGraph name deps |> Async.Catch with
            | Choice1Of2 _ -> $"%s{workerName} Completed." |> Log.success
            | Choice2Of2 ex ->
                match ex with
                | :? OperationCanceledException ->
                    let message = $"%s{workerName} Canceled."
                    failwith message
                | _ -> failwith $"%s{workerName} Failed -> %s{ex.Message}"
        with ex ->
            ex.Message |> Log.error

        // Wait for the logger to finish writing logs
        do! Async.Sleep 1000
    }
