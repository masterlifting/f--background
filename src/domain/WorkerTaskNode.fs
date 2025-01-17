﻿[<AutoOpen>]
module Worker.Domain.WorkerTaskNode

open System
open System.Threading
open Microsoft.Extensions.Configuration
open Infrastructure.Domain
open Infrastructure.Prelude

type WorkerTaskNode =
    { Name: string
      Recursively: TimeSpan option
      Parallel: bool
      Duration: TimeSpan
      Wait: bool
      Schedule: Schedule option
      Handler: (WorkerTask * IConfigurationRoot * CancellationToken -> Async<Result<WorkerTaskResult, Error'>>) option }

    member this.toOut schedule =
        { Name = this.Name
          Recursively = this.Recursively
          Parallel = this.Parallel
          Duration = this.Duration
          Schedule = schedule }

    interface Graph.INode with
        member this.Id = Graph.NodeId.New
        member this.Name = this.Name
        member this.set(_, name) = { this with Name = name }
