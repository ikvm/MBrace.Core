﻿namespace Nessos.MBrace.Store.Tests

open System
open System.Threading

open Nessos.MBrace
open Nessos.MBrace.Library
open Nessos.MBrace.Continuation
open Nessos.MBrace.Tests
open Nessos.MBrace.Store
open Nessos.MBrace.Store.Tests.TestTypes

open Nessos.FsPickler

open NUnit.Framework
open FsUnit

[<TestFixture; AbstractClass>]
type ``MBrace store tests`` () as self =

    let run wf = self.Run wf 
    let runProtected wf = 
        try self.Run wf |> Choice1Of2
        with e -> Choice2Of2 e

    abstract Run : Cloud<'T> * ?ct:CancellationToken -> 'T

    [<Test>]
    member __.``Simple CloudRef`` () = 
        let ref = run <| CloudRef.New 42
        ref.Value |> should equal 42

    [<Test>]
    member __.``Parallel CloudRef`` () =
        cloud {
            let! ref = CloudRef.New [1 .. 100]
            let! (x, y) = cloud { return ref.Value.Length } <||> cloud { return ref.Value.Length }
            return x + y
        } |> run |> should equal 200

    [<Test>]
    member __.``Distributed tree`` () =
        let tree = createTree 5 |> run
        getBranchCount tree |> run |> should equal 31


    [<Test>]
    member __.``Simple CloudSeq`` () = 
        let ref = run <| CloudSeq.New [1..10000]
        ref |> Seq.length |> should equal 10000

    [<Test>]
    member __.``Parallel CloudSeq`` () =
        let ref = run <| CloudSeq.New [1..10000]
        ref |> Seq.length |> should equal 10000
        cloud {
            let! ref = CloudSeq.New [1 .. 10000]
            let! (x, y) = cloud { return Seq.length ref } <||> cloud { return Seq.length ref }
            return x + y
        } |> run |> should equal 20000

    [<Test>]
    member __.``Simple CloudFile`` () =
        let file = CloudFile.WriteAllBytes [|1uy .. 100uy|] |> run
        file.GetSizeAsync() |> Async.RunSynchronously |> should equal 100
        cloud {
            let! bytes = CloudFile.ReadAllBytes file
            return bytes.Length
        } |> run |> should equal 100

    [<Test>]
    member __.``Large CloudFile`` () =
        let file =
            cloud {
                let text = Seq.init 1000 (fun _ -> "lorem ipsum dolor sit amet")
                return! CloudFile.WriteLines(text)
            } |> run

        cloud {
            let! lines = CloudFile.ReadLines file
            return Seq.length lines
        } |> run |> should equal 1000

    [<Test>]
    member __.``CloudFile read from stream`` () =
        let mk a = Array.init (a * 1024) byte
        let n = 512
        cloud {
            let! f = 
                CloudFile.New(fun stream -> async {
                    let b = mk n
                    stream.Write(b, 0, b.Length)
                    stream.Flush()
                    stream.Dispose() })

            let! bytes = CloudFile.ReadAllBytes(f)
            return bytes
        } |> run |> should equal (mk n)

    [<Test>]
    member __.``CloudFile get by name`` () =
        cloud {
            let! f = CloudFile.WriteAllBytes([|1uy..100uy|])
            let! t = Cloud.StartChild(cloud { 
                let! f' = CloudFile.FromPath f.Path
                return! CloudFile.ReadAllBytes f'   
            })

            return! t
        } |> run |> should equal [|1uy .. 100uy|]

    [<Test>]
    member __.``Disposable CloudFile`` () =
        cloud {
            let! file = CloudFile.WriteAllText "lorem ipsum dolor"
            do! cloud { use file = file in () }
            return! CloudFile.ReadAllText file
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``Get files in container`` () =
        cloud {
            let! container = CloudStore.GetUniqueContainerName()
            let! fileNames = CloudStore.GetFullPath(Seq.map (sprintf "file%d") [1..10], container)
            let! files =
                fileNames
                |> Seq.map (fun f -> CloudFile.WriteAllBytes([|1uy .. 100uy|], f))
                |> Cloud.Parallel

            let! files' = CloudFile.EnumerateFiles container
            return files.Length = files'.Length
        } |> run |> should equal true

    [<Test>]
    member __.``CloudFile attempt to write on stream`` () =
        cloud {
            let! cf = CloudFile.New(fun stream -> async { stream.WriteByte(10uy) })
            return! CloudFile.Read(cf, fun stream -> async { stream.WriteByte(20uy) })
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``CloudFile attempt to read nonexistent file`` () =
        cloud {
            return! CloudFile.FromPath(Guid.NewGuid().ToString())
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``CloudAtom - Sequential updates`` () =
        cloud {
            let! a = CloudAtom.New 0
            for i in 1 .. 100 do
                do! CloudAtom.Update (fun i -> i + 1) a

            return a
        } |> run |> fun a -> a.Value |> should equal 100

    [<Test; Repeat(repeats)>]
    member __.``CloudAtom - Parallel updates`` () =
        cloud {
            let! a = CloudAtom.New 0
            let worker _ = cloud {
                for _ in 1 .. 10 do
                    do! CloudAtom.Update (fun i -> i + 1) a
            }
            do! Seq.init 10 worker |> Cloud.Parallel |> Cloud.Ignore
            return a
        } |> run |> fun a -> a.Value |> should equal 100

    [<Test; Repeat(repeats)>]
    member __.``CloudAtom - Parallel updates with large obj`` () =
        cloud {
            let! isSupported = CloudAtom.IsSupportedValue [1 .. 100]
            if isSupported then return true
            else
                let! atom = CloudAtom.New List.empty<int>
                do! Seq.init 100 (fun i -> CloudAtom.Update (fun is -> i :: is) atom) |> Cloud.Parallel |> Cloud.Ignore
                return List.sum atom.Value = List.sum [1..100]
        } |> run |> should equal true

    [<Test; Repeat(repeats)>]
    member __.``CloudAtom - transact with contention`` () =
        cloud {
            let! a = CloudAtom.New 0
            let! results = Seq.init 100 (fun _ -> CloudAtom.Transact(fun i -> i, (i+1)) a) |> Cloud.Parallel
            return Array.sum results
        } |> run |> should equal (Array.sum [|0 .. 99|])

    [<Test; Repeat(repeats)>]
    member __.``CloudAtom - force with contention`` () =
        cloud {
            let! a = CloudAtom.New 0
            do! Seq.init 100 (fun i -> CloudAtom.Force i a) |> Cloud.Parallel |> Cloud.Ignore
            return a.Value
        } |> run |> should be (greaterThan 50)

    [<Test; Repeat(repeats)>]
    member __.``CloudAtom - dispose`` () =
        cloud {
            let! a = CloudAtom.New 0
            do! cloud { use a = a in () }
            return! CloudAtom.Read a
        } |> runProtected |> Choice.shouldFailwith<_,exn>


[<TestFixture; AbstractClass>]
type ``Local MBrace store tests`` (fileStore, tableStore) =
    inherit ``MBrace store tests``()

    let ctx = StoreConfiguration.mkExecutionContext fileStore tableStore

    override __.Run(wf : Cloud<'T>, ?ct) = Cloud.RunSynchronously(wf, resources = ctx, ?cancellationToken = ct)