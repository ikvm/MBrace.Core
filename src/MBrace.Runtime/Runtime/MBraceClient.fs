﻿namespace MBrace.Runtime

open MBrace.Core
open MBrace.Core.Internals
open MBrace.ThreadPool
open MBrace.Runtime.Utils

/// MBrace runtime client handle abstract class.
[<AbstractClass; NoEquality; NoComparison; AutoSerializable(false)>]
type MBraceClient (runtime : IRuntimeManager) =
    do ignore <| RuntimeManagerRegistry.TryRegister runtime

    let syncRoot = new obj()
    let checkVagabondDependencies (graph:obj) = runtime.AssemblyManager.ComputeDependencies graph |> ignore
    let imem = ThreadPoolRuntime.Create(resources = runtime.ResourceRegistry, memoryEmulation = MemoryEmulation.Shared, vagabondChecker = checkVagabondDependencies)
    let storeClient = CloudStoreClient.Create(imem)

    let taskManagerClient = new CloudTaskManagerClient(runtime)
    let getWorkers () = async {
        let! workers = runtime.WorkerManager.GetAvailableWorkers()
        return workers |> Array.map (fun w -> WorkerRef.Create(runtime, w.Id))
    }

    let workers = CacheAtom.Create(getWorkers(), intervalMilliseconds = 500)

    let mutable systemLogPoller : ILogPoller<SystemLogEntry> option = None
    let getSystemLogPoller() =
        match systemLogPoller with
        | Some lp -> lp
        | None ->
            lock syncRoot (fun () ->
                match systemLogPoller with
                | Some lp -> lp
                | None ->
                    let lp = runtime.RuntimeSystemLogManager.CreateLogPoller() |> Async.RunSync
                    systemLogPoller <- Some lp
                    lp)

    /// <summary>
    ///     Creates a fresh cloud cancellation token source for use in the MBrace cluster.
    /// </summary>
    /// <param name="parents">Parent cancellation token sources. New cancellation token will be canceled if any of the parents is canceled.</param>
    member c.CreateCancellationTokenSource (?parents : seq<ICloudCancellationToken>) : ICloudCancellationTokenSource =
        async {
            let parents = parents |> Option.map Seq.toArray
            let! dcts = CloudCancellationToken.Create(runtime.CancellationEntryFactory, ?parents = parents, elevate = true)
            return dcts :> ICloudCancellationTokenSource
        } |> Async.RunSync

    /// <summary>
    ///     Asynchronously submits supplied cloud workflow for execution in the current MBrace runtime.
    ///     Returns an instance of CloudTask, which can be queried for information on the progress of the computation.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to single retry.</param>
    /// <param name="target">Target worker to initialize computation.</param>
    /// <param name="additionalResources">Additional per-task MBrace resources that can be appended to the computation state.</param>
    /// <param name="taskName">User-specified process name.</param>
    member c.CreateTaskAsync(workflow : Cloud<'T>, ?cancellationToken : ICloudCancellationToken, 
                                    ?faultPolicy : FaultPolicy, ?target : IWorkerRef, 
                                    ?additionalResources : ResourceRegistry, ?taskName : string) : Async<CloudTask<'T>> = async {

        let faultPolicy = match faultPolicy with Some fp -> fp | None -> FaultPolicy.Retry(maxRetries = 1)
        let dependencies = runtime.AssemblyManager.ComputeDependencies((workflow, faultPolicy))
        let assemblyIds = dependencies |> Array.map (fun d -> d.Id)
        do! runtime.AssemblyManager.UploadAssemblies(dependencies)
        return! Combinators.runStartAsCloudTask runtime None assemblyIds taskName faultPolicy cancellationToken additionalResources target workflow
    }

    /// <summary>
    ///     Submits supplied cloud workflow for execution in the current MBrace runtime.
    ///     Returns an instance of CloudTask, which can be queried for information on the progress of the computation.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to single retry.</param>
    /// <param name="target">Target worker to initialize computation.</param>
    /// <param name="additionalResources">Additional per-task MBrace resources that can be appended to the computation state.</param>
    /// <param name="taskName">User-specified process name.</param>
    member __.CreateTask(workflow : Cloud<'T>, ?cancellationToken : ICloudCancellationToken, ?faultPolicy : FaultPolicy, 
                                ?target : IWorkerRef, ?additionalResources : ResourceRegistry, ?taskName : string) : CloudTask<'T> =
        __.CreateTaskAsync(workflow, ?cancellationToken = cancellationToken, ?faultPolicy = faultPolicy, 
                                    ?target = target, ?additionalResources = additionalResources, ?taskName = taskName) |> Async.RunSync


    /// <summary>
    ///     Asynchronously submits a cloud workflow for execution in the current MBrace runtime
    ///     and waits until the computation completes with a value or fails with an exception.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to single retry.</param>
    /// <param name="target">Target worker to initialize computation.</param>
    /// <param name="additionalResources">Additional per-task MBrace resources that can be appended to the computation state.</param>
    /// <param name="taskName">User-specified process name.</param>
    member __.RunAsync(workflow : Cloud<'T>, ?cancellationToken : ICloudCancellationToken, ?faultPolicy : FaultPolicy, ?additionalResources : ResourceRegistry, ?target : IWorkerRef, ?taskName : string) : Async<'T> = async {
        let! task = __.CreateTaskAsync(workflow, ?cancellationToken = cancellationToken, ?faultPolicy = faultPolicy, ?target = target, ?additionalResources = additionalResources, ?taskName = taskName)
        return! task.AwaitResult()
    }

    /// <summary>
    ///     Submits a cloud workflow for execution in the current MBrace runtime
    ///     and waits until the computation completes with a value or fails with an exception.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to single retry.</param>
    /// <param name="target">Target worker to initialize computation.</param>
    /// <param name="additionalResources">Additional per-task MBrace resources that can be appended to the computation state.</param>
    /// <param name="taskName">User-specified process name.</param>
    member __.Run(workflow : Cloud<'T>, ?cancellationToken : ICloudCancellationToken, ?faultPolicy : FaultPolicy, ?target : IWorkerRef, ?additionalResources : ResourceRegistry, ?taskName : string) : 'T =
        __.RunAsync(workflow, ?cancellationToken = cancellationToken, ?faultPolicy = faultPolicy, ?target = target, ?additionalResources = additionalResources, ?taskName = taskName) |> Async.RunSync

    /// Gets a collection of all running or completed cloud tasks that exist in the current MBrace runtime.
    member __.GetAllTasks () : CloudTask [] = taskManagerClient.GetAllTasks() |> Async.RunSync

    /// <summary>
    ///     Attempts to get a Cloud task instance using supplied identifier.
    /// </summary>
    /// <param name="id">Input task identifier.</param>
    member __.TryGetTaskById(taskId:string) = taskManagerClient.TryGetTask(taskId) |> Async.RunSync

    /// <summary>
    ///     Looks up a CloudTask instance from cluster using supplied identifier.
    /// </summary>
    /// <param name="taskId">Input task identifier.</param>
    member __.GetTaskById(taskId:string) = 
        match __.TryGetTaskById taskId with
        | None -> raise <| invalidArg "taskId" "No task with supplied id could be found in cluster."
        | Some t -> t

    /// <summary>
    ///     Deletes cloud task and all related data from MBrace cluster.
    /// </summary>
    /// <param name="task">Cloud task to be cleared.</param>
    member __.ClearTask(task:CloudTask<'T>) : unit = taskManagerClient.ClearTask(task) |> Async.RunSync

    /// <summary>
    ///     Deletes *all* cloud tasks and related data from MBrace cluster.
    /// </summary>
    member __.ClearAllTasks() : unit = taskManagerClient.ClearAllTasks() |> Async.RunSync

    /// Gets a printed report of all current cloud tasks.
    member __.FormatTasks() : string = taskManagerClient.GetTaskInfo()

    /// Prints a report of all current cloud tasks to stdout.
    member __.ShowTasks() : unit = taskManagerClient.ShowTaskInfo()

    /// Gets a client object that can be used for interoperating with the MBrace store.
    member __.Store : CloudStoreClient = storeClient

    /// Gets all available workers for the MBrace runtime.
    member __.Workers : WorkerRef [] = workers.Value

    /// Gets a printed report on all workers on the runtime
    member __.FormatWorkers() = WorkerReporter.Report(__.Workers, title = "Workers", borders = false)

    /// Prints a report on all workers on the runtime to stdout
    member __.ShowWorkers() = System.Console.WriteLine(__.FormatWorkers())

    /// Resolves runtime resource of given type
    member __.GetResource<'TResource> () : 'TResource = runtime.ResourceRegistry.Resolve<'TResource> ()

    /// <summary>
    ///     Asynchronously executes supplied cloud workflow within the current, client process.
    ///     Parallelism is afforded through the .NET thread pool.
    /// </summary>
    /// <param name="workflow">Cloud workflow to execute.</param>
    /// <param name="memoryEmulation">Specify memory emulation semantics for local parallelism. Defaults to shared memory.</param>
    member __.RunOnCurrentProcessAsync(workflow : Cloud<'T>, ?memoryEmulation : MemoryEmulation) : Async<'T> =
        imem.ToAsync(workflow, ?memoryEmulation = memoryEmulation)

    /// <summary>
    ///     Asynchronously executes supplied cloud workflow within the current, client process.
    ///     Parallelism is afforded through the .NET thread pool.
    /// </summary>
    /// <param name="workflow">Cloud workflow to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="memoryEmulation">Specify memory emulation semantics for local parallelism. Defaults to shared memory.</param>
    member __.RunOnCurrentProcess(workflow : Cloud<'T>, ?cancellationToken : ICloudCancellationToken, ?memoryEmulation : MemoryEmulation) : 'T = 
        imem.RunSynchronously(workflow, ?cancellationToken = cancellationToken, ?memoryEmulation = memoryEmulation)

    /// <summary>
    ///     Attaches user-supplied logger to client instance.
    ///     Returns an unsubscribe token if successful.
    /// </summary>
    /// <param name="logger">Logger instance to be attached.</param>
    member __.AttachLogger(logger : ISystemLogger) : System.IDisposable = runtime.LocalSystemLogManager.AttachLogger logger

    /// Gets or sets the system log level used by the client process.
    member __.LogLevel
        with get () = runtime.LocalSystemLogManager.LogLevel
        and set l = runtime.LocalSystemLogManager.LogLevel <- l

    /// <summary>
    ///     Asynchronously fetches all system logs generated by all workers in the MBrace runtime.
    /// </summary>
    /// <param name="logLevel">Maximum log level to display. Defaults to LogLevel.Info.</param>
    /// <param name="filter">User-specified log filtering function.</param>
    member __.GetSystemLogsAsync(?logLevel : LogLevel, ?filter : SystemLogEntry -> bool) = async {
        let filter = defaultArg filter (fun _ -> true)
        let logLevel = defaultArg logLevel LogLevel.Info
        let! entries = runtime.RuntimeSystemLogManager.GetRuntimeLogs()
        return entries |> Seq.filter (fun e -> e.LogLevel <= logLevel && filter e) |> Seq.toArray
    }

    /// <summary>
    ///     Fetches all system logs generated by all workers in the MBrace runtime.
    /// </summary>
    /// <param name="logLevel">Maximum log level to display. Defaults to LogLevel.Info.</param>
    /// <param name="filter">User-specified log filtering function.</param>
    member __.GetSystemLogs(?logLevel : LogLevel, ?filter : SystemLogEntry -> bool) : SystemLogEntry[] =
        __.GetSystemLogsAsync(?logLevel = logLevel, ?filter = filter) |> Async.RunSync

    /// <summary>
    ///     Prints all system logs generated by all workers in the cluster to stdout.
    /// </summary>
    /// <param name="logLevel">Maximum log level to display. Defaults to LogLevel.Info.</param>
    /// <param name="filter">User-specified log filtering function.</param>
    member __.ShowSystemLogs(?logLevel : LogLevel, ?filter : SystemLogEntry -> bool) : unit =
        let filter = defaultArg filter (fun _ -> true)
        let logLevel = defaultArg logLevel LogLevel.Info
        runtime.RuntimeSystemLogManager.GetRuntimeLogs()
        |> Async.RunSync
        |> Seq.filter (fun e -> e.LogLevel <= logLevel && filter e)
        |> Seq.map (fun e -> SystemLogEntry.Format(e, showDate = true, showSourceId = true))
        |> Seq.iter System.Console.WriteLine

    /// Event for subscribing to runtime-wide system logs
    [<CLIEvent>]
    member __.SystemLogs = getSystemLogPoller() :> IEvent<SystemLogEntry>

    /// <summary>
    ///     Registers a native assembly dependency to client state.
    /// </summary>
    /// <param name="assemblyPath">Path to native assembly.</param>
    member __.RegisterNativeDependency(assemblyPath : string) : unit =
        ignore <| runtime.AssemblyManager.RegisterNativeDependency assemblyPath

    /// Gets native assembly dependencies registered to client state.
    member __.NativeDependencies : string [] =
        runtime.AssemblyManager.NativeDependencies |> Array.map (fun v -> v.Image)

    /// Resets cluster state. This will cancel and delete all task data.
    member __.Reset() = runtime.ResetClusterState()