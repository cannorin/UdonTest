open VRC.Udon
open FSharpPlus
open FSharp.Scanf

let runTest () =
  let myWrapper =
    let print =
      Common.Delegates.UdonExternDelegate(fun heap stack ->
        stack.[0] |> heap.GetHeapVariable<obj> |> printfn "My.Print> %A"
      )
    { new Common.Interfaces.IUdonWrapperModule with
        member __.Name = "My"
        member __.GetExternFunctionParameterCount(signature) =
          match signature with
          | "Print" -> 1
          | _ -> invalidArg "signature" (sprintf "no impl for sig %s" signature)
        member __.GetExternFunctionDelegate(signature) =
          match signature with
          | "Print" -> print
          | _ -> invalidArg "signature" (sprintf "no impl for sig %s" signature)
    }

  let src = """
  .data_start
    foo: %SystemInt32, null
    bar: %SystemString, null
  .data_end

  .code_start
    .export _start
    _start:
    PUSH, foo
    PUSH, bar
    EXTERN, "SystemConvert.__ToString__SystemInt32__SystemString"
    PUSH, bar
    EXTERN, "My.Print"
    JUMP, 0xFFFFFF
  .code_end
  """

  let asm = UAssembly.Assembler.UAssemblyAssembler()
  let program = asm.Assemble src
  let fooAddr = program.SymbolTable.GetAddressFromSymbol "foo"
  let barAddr = program.SymbolTable.GetAddressFromSymbol "bar"
  let dwf = Wrapper.UdonDefaultWrapperFactory()
  dwf.RegisterWrapperModule myWrapper
  let uvf = VM.UdonVMFactory(dwf)
  let vm = uvf.ConstructUdonVM()

  if vm.LoadProgram program then
    let heap = vm.InspectHeap()
    heap.SetHeapVariable<int>(fooAddr, 42)
    vm.Interpret() |> ignore
  else
    printfn "fail"

open System.Reflection

type ExternType =
  | StaticFunc of args:string[] * ret:string
  | StaticVoidRetFunc of arg:string[]
  | StaticVoidArgFunc of ret:string
  | StaticVoidRetArgFunc
  | InstanceFunc of args:string[] * ret:string
  | InstanceVoidRetFunc of arg:string[]
  | InstanceVoidArgFunc of ty:string
  | InstanceVoidRetArgFunc
  | Constructor of args:string[] * ty:string
  | Unknown of arity:int * argret:string[][]
 
let parseSignature (name: string) (argret: string[][]) (arity: int) =
  match name, argret with
  | "ctor", [| xs; [|ret|] |] when xs.Length + 1 = arity -> Constructor (xs, ret)
  | _, [|[|"SystemVoid"|]|] ->
    if arity = 0 then StaticVoidRetArgFunc
    else if arity = 1 then InstanceVoidRetArgFunc
    else Unknown (arity, argret)
  | _, [|[|ret|]|] ->
    if arity = 1 then StaticVoidArgFunc ret
    else if arity = 2 then InstanceVoidArgFunc ret
    else Unknown (arity, argret)
  | _, [| args; [|"SystemVoid"|] |] ->
    if arity = args.Length then StaticVoidRetFunc args
    else if arity = args.Length + 1 then InstanceVoidRetFunc args
    else Unknown (arity, argret)
  | _, [| args; [|ret|] |] ->
    if arity = args.Length + 1 then StaticFunc (args, ret)
    else if arity = args.Length + 2 then InstanceFunc (args, ret)
    else Unknown (arity, argret)
  | _ -> Unknown (arity, argret)

let enumerateExterns () =
  let asm = (typeof<Wrapper.UdonWrapper>).Assembly
  let wrapperTy = typeof<Common.Interfaces.IUdonWrapperModule>
  let name = wrapperTy.GetProperty("Name")
  let types =
    asm.GetTypes()
    |> Seq.filter (fun t -> t.Namespace = "VRC.Udon.Wrapper.Modules" && t.Name.StartsWith "Extern")
    |> Seq.choose (fun t ->
      let ctor = t.GetConstructor([||]) |> Option.ofObj
      let pc =
        t.GetField("_parameterCounts", BindingFlags.NonPublic ||| BindingFlags.Static)
        |> Option.ofObj
      Option.map2 (fun x y -> x, y) pc ctor
    )
    |> Seq.map (fun (pc, ctor) ->
      let instance = ctor.Invoke([||])
      let name = name.GetValue(instance) :?> string
      let dict = pc.GetValue() :?> System.Collections.Generic.Dictionary<string, int>
      dict |> Seq.map (function KeyValue(k, v) -> name,k,v)
      )
    |> Seq.concat
    |> Seq.map (fun (name, signature, argcount) ->
      let fn, xs =
        match signature |> String.split ["__"] |> Seq.filter ((<>) "") |> Seq.toList with
        | [] -> failwith "impossible"
        | x :: xs ->
          x, 
          xs |> Seq.map (fun x -> x.Split '_')
             |> Seq.toArray
      name,fn,xs,signature,argcount
      )
  for moduleName, funcName, xs, orig, arity in types do
    let ty = parseSignature funcName xs arity
    match ty with
    | Unknown _ -> () // printfn "%s.%s: Unknown, arity = %i, orig = %s.%s" moduleName funcName arity moduleName orig
    | _ -> () // printfn "%s.%s :: %A, orig = %s.%s" moduleName funcName ty moduleName orig
  types |> Seq.length |> printfn "%i"

enumerateExterns()