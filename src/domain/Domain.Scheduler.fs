﻿[<AutoOpen>]
module Worker.Domain.Scheduler

open System

type WorkerSchedulerStopReason =
    | NotWorkday of DayOfWeek
    | StopDateReached of DateOnly
    | StopTimeReached of TimeOnly

    member this.Message =
        match this with
        | NotWorkday day -> $"Not workday: {day}"
        | StopDateReached date -> $"Stop date reached: {date}"
        | StopTimeReached time -> $"Stop time reached: {time}"

type WorkerScheduler =
    | NotScheduled
    | Started of WorkerSchedule
    | StartIn of TimeSpan * WorkerSchedule
    | Stopped of WorkerSchedulerStopReason * WorkerSchedule
    | StopIn of TimeSpan * WorkerSchedule
