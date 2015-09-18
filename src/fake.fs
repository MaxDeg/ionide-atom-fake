[<ReflectedDefinition>]
module Ionide.Fake

open System
open System.Text.RegularExpressions
open FunScript
open FunScript.TypeScript
open FunScript.TypeScript.fs
open FunScript.TypeScript.child_process
open FunScript.TypeScript.AtomCore
open FunScript.TypeScript.text_buffer
open Atom
open Atom.FSharp

[<ReflectedDefinition>]
module FakeService =
    type BuildData = {Name : string; Start : DateTime; mutable End : DateTime option; mutable Output : string; mutable TextEditor : IEditor option}

    let mutable private File : (string * string) option = None
    let mutable private taskListView : (atom.SelectListView * IPanel) option = None
    let mutable private buildListView : (atom.SelectListView * IPanel) option = None
    let mutable private BuildList = ResizeArray()


    let private viewForItem (desc : ListView.ItemDescription) =
        if JS.isDefined desc then
            sprintf "<li>%s</li>" desc.data |> jq
        else
            "<li></li>" |> jq

    let private startBuild (packageDescription :  ListView.ItemDescription) =
        File |> Option.iter( fun (build, fake) ->
            let data = {Name = packageDescription.data; Start = DateTime.Now; End = None; Output = ""; TextEditor = None}
            BuildList.Add data
            let fakeProcess = Process.spawnWithNotifications build "sh" packageDescription.data
            fakeProcess.on("exit",unbox<Function>(fun _ -> data.End <- Some DateTime.Now)) |> ignore
            fakeProcess.stdout.on("data", unbox<Function>(fun e ->
                data.Output <- data.Output + e.ToString()
                data.TextEditor |> Option.iter (fun te ->
                    let b = te.getBuffer()
                    e.ToString() |> b.append |> ignore
                    )
                )) |> ignore
            fakeProcess.stderr.on("data", unbox<Function>(fun e ->
                data.Output <- data.Output + e.ToString()
                data.TextEditor |> Option.iter (fun te ->
                    let b = te.getBuffer()
                    e.ToString() |> b.append |> ignore
                )
            )) |> ignore
        )
        ()

    let private registerTaskList () =
        let stopChangingCallback (ev : IEditor) (lv : atom.SelectListView) = fun () -> ()
        let cancelledCallback = Func<_>(fun _ -> taskListView |> Option.iter(fun (model, view) ->  view.hide()) :> obj)
        let confirmedCallback = unbox<Func<_, _>> (fun (packageDescription : ListView.ItemDescription) ->
            taskListView |> Option.iter (fun (model, view) -> view.hide())
            startBuild packageDescription
        )
        ListView.regiterListView stopChangingCallback cancelledCallback confirmedCallback viewForItem false

    let private registerBuildList () =
        let stopChangingCallback (ev : IEditor) (lv : atom.SelectListView) = fun () -> ()
        let cancelledCallback = Func<_>(fun _ -> buildListView |> Option.iter(fun (model, view) ->  view.hide()) :> obj)
        let confirmedCallback = unbox<Func<_, _>> (fun (buildDescription : ListView.ItemDescription) ->
            buildListView |> Option.iter (fun (model, view) -> view.hide())
            let build = BuildList |> Seq.find(fun n ->
                let dateString = n.Start.ToShortDateString().Replace("\\",".").Replace("/",".")
                let timeString = n.Start.ToShortTimeString().Replace("\\",".").Replace("/", ".")
                let desc = sprintf "%s - %s %s" n.Name dateString timeString
                desc = buildDescription.data)

            Globals.atom.workspace._open(buildDescription.data, {split = "right"})._done((fun ed ->
                build.TextEditor <- Some ed
                ed.insertText build.Output |> ignore
                ed.onDidDestroy((fun _ -> build.TextEditor <- None) |> unbox<Function>) |> ignore
                let view = Globals.atom.views.getView ed
                setComponentEnabled(view, false)) |> unbox<Function>)
            ()
        )
        ListView.regiterListView stopChangingCallback cancelledCallback confirmedCallback viewForItem false

    let private BuildTask () =
        taskListView |> Option.iter(fun (model, view) ->
        File |> Option.iter(fun (build, fake) ->
        view.show()
        model.focusFilterEditor() |> ignore
        let script = fake |> Globals.readFileSync
                          |> fun n -> n.toString()
        let matches = Regex.Matches(script, "Target \"([\\w.]+)\"") |> Seq.cast<Match> |> Seq.toArray
        let m = matches |> Array.map(fun m -> {ListView.data = m.Groups.[1].Value} :> obj)
        model.setItems m |> ignore
        ()
        ))

    let private BuildDefault () =
        let packageDescription = {ListView.data = "" }
        startBuild packageDescription

    let private ShowBuildList () =
        buildListView |> Option.iter(fun (model, view) ->
            view.show()
            model.focusFilterEditor() |> ignore
            let m = BuildList
                    |> Seq.sortBy(fun n -> n.Start)
                    |> Seq.map(fun n ->
                        let dateString = n.Start.ToShortDateString().Replace("\\",".").Replace("/",".")
                        let timeString = n.Start.ToShortTimeString().Replace("\\",".").Replace("/", ".")

                        let name = sprintf "%s - %s %s" n.Name dateString timeString
                        {ListView.data = name } :> obj)
                    |> Seq.toArray
                    |> Array.rev

            model.setItems m |> ignore
            ()
        )

    let private FAKENotFound () =
        Process.notice (ref None) true "FAKE error" "FAKE script not found"

    let activate () =
        taskListView <- registerTaskList () |> Some
        buildListView <- registerBuildList () |> Some
        let p = Globals.atom.project.getPaths().[0]
        let proj (ex : NodeJS.ErrnoException) (arr : string array) =
            let ext = if Process.isWin() then "cmd" else "sh"
            let projExist = if JS.isDefined arr then arr |> Array.tryFind(fun a -> a.Split('.') |> fun n -> if JS.isDefined n then n.[n.Length - 1] = ext else false)
                            else None
            match projExist with
            | Some a ->
                let path = p + "/" + a
                let file = path |> Globals.readFileSync
                                |> fun n -> n.toString()
                let regex = Regex.Matches(file, "FAKE.exe.* ([\w]+\.fsx)")
                if regex.Count > 0 then
                    [0 .. regex.Count - 1] |> Seq.iter (fun i ->
                        if File.IsNone then
                            let build = p + "/" + regex.[i].Groups.[1].Value
                            if Globals.existsSync build then
                                File <- Some (path, build )
                                Globals.atom.commands.add("atom-workspace", "FAKE:Build", BuildTask |> unbox<Function>) |> ignore
                                Globals.atom.commands.add("atom-workspace", "FAKE:Build-Default", BuildDefault |> unbox<Function>) |> ignore
                                Globals.atom.commands.add("atom-workspace", "FAKE:Show-Builds", ShowBuildList |> unbox<Function>) |> ignore
                    )
                else
                    Globals.atom.commands.add("atom-workspace", "FAKE: Build", FAKENotFound |> unbox<Function>) |> ignore
            | None -> Globals.atom.commands.add("atom-workspace", "FAKE: Build", FAKENotFound |> unbox<Function>) |> ignore

        if JS.isDefined p then Globals.readdir(p, System.Func<NodeJS.ErrnoException, string array, unit>(proj))



type Fake() =


    member x.activate(state:obj) =
        FakeService.activate ()
        ()

    member x.deactivate() =
        ()
