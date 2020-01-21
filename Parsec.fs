module Parsec

let [<Literal>] EOS = '\uffff'

type StringSegment = {
  startIndex: int
  length: int
  underlying: string
  startLine: int
  startColumn: int
} with
  member inline this.Value = this.underlying.Substring(this.startIndex, this.length)
  member inline this.Item index =
    if index < 0 || index >= this.length then failwith "Index was out of range (Item)."
    else this.underlying.[this.startIndex + index]
  member inline this.GetSafe index =
    if index < 0 || index >= this.length then EOS
    else this.underlying.[this.startIndex + index]
  member inline this.GetSafeOverOriginal index =
    let i = this.startIndex + index
    if i >= 0 && i < this.underlying.Length then this.underlying.[i]
    else EOS
  member inline this.GetSlice (start, finish) =
    let start = defaultArg start 0
    let finish = defaultArg finish (this.length-1)
    let inline check x = x >= 0 && x < this.length
    if    start >= 0 && start <= this.length
       && finish < max start this.length then
      let len = max 0 (finish - start + 1)
      let mutable line = this.startLine
      let mutable column = this.startColumn
      for i = 0 to start - 1 do
        match this.underlying.[this.startIndex + i] with
        | '\n' -> line <- line + 1; column <- 0
        | _ -> column <- column + 1
      { underlying = this.underlying
        startIndex = this.startIndex + start
        length = len
        startLine = line
        startColumn = column }
    else failwithf "Index was out of range (GetSlice(%i, %i))." start finish

module StringSegment =
  let inline private normalize (str: string) =
    str.Replace("\r\n", "\n").Replace("\r", "\n")
  let inline ofString (str: string) =
    let str = normalize str
    { underlying = str; startIndex = 0; length = str.Length; startLine = 0; startColumn = 0 }
  let inline toString (seg: StringSegment) = seg.Value
  let inline isEmpty (seg: StringSegment) = seg.length <= 0
  let startsWith (s: string) (seg: StringSegment) =
    let rec check i =
      if i = s.Length then true
      else if i = seg.length || seg.underlying.[i + seg.startIndex] <> s.[i] then false
      else check (i+1)
    check 0
  let indexOfItem (c: char) (seg: StringSegment) =
    let rec check i =
      if i = seg.length then -1
      else if seg.underlying.[i + seg.startIndex] = c then i
      else check (i+1)
    check 0
  let indexOfAny (cs: char seq) (seg: StringSegment) =
    let s = Set.ofSeq cs
    let rec check i =
      if i = seg.length then -1
      else if s |> Set.contains seg.underlying.[i + seg.startIndex] then i
      else check (i+1)
    check 0
  let indexOfSequence (s: string) (seg: StringSegment) =
    let rec check i j =
      if j = s.Length then i - j
      else if i + s.Length - j > seg.length then -1
      else if seg.underlying.[i + seg.startIndex] = s.[j] then check (i+1) (j+1)
      else check (i+1) 0
    check 0 0
  let inline subString start length (seg: StringSegment) = seg.[start..length-start+1]
  let inline skip length (seg: StringSegment) = seg.[length..]
  let inline getSafe i (seg: StringSegment) = seg.GetSafe i

type StringSegment with
  member inline this.StartsWith (s: string) = StringSegment.startsWith s this
  member inline this.IndexOf (c: char) = StringSegment.indexOfItem c this
  member inline this.IndexOf (s: string) = StringSegment.indexOfSequence s this
  member inline this.IndexOfAny (cs: char seq) = StringSegment.indexOfAny cs this

type Position = Position of line:int * col:int

type StringSegment with
  member inline this.pos = Position (this.startLine, this.startColumn)

type Message = Lazy<string>

type ErrorType =
  | Expected of string
  | Unexpected of string
  | Message of string

type ErrorMessage = ErrorType list
type ParseError<'State> = (Position * ErrorMessage) list * 'State
type ParseResult<'Result, 'State> = 'Result * StringSegment * 'State

type Parser<'Result, 'State> =
  ('State * StringSegment -> Result<ParseResult<'Result, 'State>, ParseError<'State>>)
type Parser<'Result> = Parser<'Result, unit>

module ParseError =
  let prettyPrint ((es, _): ParseError<_>) (input: string option) =
    let input = input |> Option.map (fun s -> s.Split '\n')
    [
      for (Position (line, col), msgs) in es do
        yield sprintf "at Line %i, Col %i:\n" line col
        match input with
        | None -> ()
        | Some lines -> yield sprintf "> %s" lines.[line]
        for msg in msgs do
          match msg with
          | Expected   label -> yield sprintf "    Expected %s.\n" label
          | Unexpected label -> yield sprintf "    Unexpected %s.\n" label
          | Message msg -> yield sprintf "  %s\n" msg
    ]
    |> String.concat ""

let inline run       (p: Parser<'a, 's>) state input = p (state, input)
let inline runString (p: Parser<'a, 's>) state input = p (state, StringSegment.ofString input)

[<AutoOpen>]
module Primitives =
  let inline preturn (x: 'a) : Parser<'a, 's> = fun (state, s) -> Ok (x, s, state)
  let inline pzero (state, s: StringSegment) = Error (s.startIndex, lazy "pzero", state)

  let inline ( >>= ) (p: Parser<'a, 's>) (f: 'a -> Parser<'b, 's>) : Parser<'b, 's> =
    fun (state, s) ->
      match run p state s with
      | Error e -> Error e
      | Ok (r, s, state) -> run (f r) state s

  let inline ( >>% ) (p: Parser<'a, 's>) (x: 'b) : Parser<'b, 's> =
    fun (state, s) ->
      match run p state s with
      | Error e -> Error e
      | Ok (_, s, state) -> Ok (x, s, state)

  let inline ( >>. ) (p1: Parser<'a, 's>) (p2: Parser<'b, 's>) : Parser<'b, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> Error e
      | Ok (_, s, state) ->
        match run p2 state s with
        | Error e -> Error e
        | Ok (r2, s, state) -> Ok (r2, s, state)

  let inline ( .>> ) (p1: Parser<'a, 's>) (p2: Parser<'b, 's>) : Parser<'a, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> Error e
      | Ok (r1, s, state) ->
        match run p2 state s with
        | Error e -> Error e
        | Ok (_, s, state) -> Ok (r1, s, state)

  let inline ( .>>. ) (p1: Parser<'a, 's>) (p2: Parser<'b, 's>) : Parser<'a * 'b, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> Error e
      | Ok (r1, s, state) ->
        match run p2 state s with
        | Error e -> Error e
        | Ok (r2, s, state) -> Ok ((r1, r2), s, state)

  let inline between pl pr p = pl >>. p .>> pr

  let inline ( |>> ) (p: Parser<'a, 's>) (f: 'a -> 'b) : Parser<'b, 's> =
    fun (state, s) ->
      match run p state s with
      | Error e -> Error e
      | Ok (r, s, state) -> Ok (f r, s, state)

  let inline pipe2 (p1: Parser<'a, 's>) (p2: Parser<'b, 's>) (f: 'a -> 'b -> 'c) : Parser<'c, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> Error e
      | Ok (r1, s, state) ->
        match run p2 state s with
        | Error e -> Error e
        | Ok (r2, s, state) -> Ok (f r1 r2, s, state)

  let inline pipe3 p1 p2 p3 f : Parser<'r, 's> =
    p1 >>= fun r1 ->
      p2 >>= fun r2 ->
        p3 >>= fun r3 -> f r1 r2 r3 |> preturn

  let inline pipe4 p1 p2 p3 p4 f : Parser<'r, 's> =
    p1 >>= fun r1 ->
      p2 >>= fun r2 ->
        p3 >>= fun r3 ->
          p4 >>= fun r4 -> f r1 r2 r3 r4 |> preturn

  let inline pipe5 p1 p2 p3 p4 p5 f : Parser<'r, 's> =
    p1 >>= fun r1 ->
      p2 >>= fun r2 ->
        p3 >>= fun r3 ->
          p4 >>= fun r4 ->
            p5 >>= fun r5 -> f r1 r2 r3 r4 r5 |> preturn

  let inline ( <|> ) (p1: Parser<'a, 's>) (p2: Parser<'a, 's>) : Parser<'a, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Ok _ as x -> x
      | Error (es, state) ->
        match run p2 state s with
        | Ok _ as x -> x
        | Error (es', state) -> Error (es @ es', state)

  let inline ( <|>% ) (p: Parser<'a, 's>) (x: 'a) : Parser<'a, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ as x -> x
      | Error (_, state) -> Ok (x, s, state)

  let inline internal err1 pos msg state = Error ([pos, [msg]], state)
  let inline internal quote s = "'" + s + "'"

  let inline choice (ps: Parser<'a, 's> list) : Parser<'a, 's> =
    fun (state, s) ->
      let rec go errorsAcc = function
        | [] -> err1 s.pos (Message "No parsers given") state
        | p :: [] ->
          match run p state s with
          | Ok _ as x -> x
          | Error (errors, _) -> Error (errorsAcc @ errors, state)
        | p :: ps ->
          match run p state s with
          | Ok _ as x -> x
          | Error (errors, _) -> go (errors @ errorsAcc) ps
      go [] ps

  let inline choiceL (ps: Parser<'a, 's> list) (label: string) : Parser<'a, 's> =
    fun (state, s) ->
      let rec go = function
        | [] -> err1 s.pos (Message "No parsers given") state
        | p :: [] ->
          match run p state s with
          | Ok _ as x -> x
          | Error (_, _) -> Error ([s.pos, [Expected label]], state)
        | p :: ps ->
          match run p state s with
          | Ok _ as x -> x
          | Error _ -> go ps
      go ps

  let inline opt (p: Parser<'a, 's>) : Parser<'a option, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok (r, s, state) -> Ok (Some r, s, state)
      | Error _ -> Ok (None, s, state)

  let inline optional (p: Parser<'a, 's>) : Parser<unit, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok (_, s, state) -> Ok ((), s, state)
      | Error _ -> Ok ((), s, state)

  let inline notEmpty (p: Parser<'a, 's>) : Parser<'a, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok (_, s', _) as x when s.pos <> s'.pos || s.length <> s'.length -> x
      | Ok _ -> err1 s.pos (Message "Parser succeeded without consuming input") state
      | Error _ as x -> x

  let inline followedBy (p: Parser<'a, 's>) : Parser<unit, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ -> Ok ((), s, state)
      | Error e -> Error e

  let inline followedByL (p: Parser<'a, 's>) (label: string) : Parser<unit, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ -> Ok ((), s, state)
      | Error (_, state) -> err1 s.pos (Expected label) state

  let inline notFollowedBy (p: Parser<'a, 's>) : Parser<unit, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ -> err1 s.pos (Message "notFollowedBy failed") state
      | Error _ -> Ok ((), s, state)

  let inline notFollowedByL (p: Parser<'a, 's>) (label: string) : Parser<unit, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ -> err1 s.pos (Unexpected label) state
      | Error _ -> Ok ((), s, state)

  let inline lookAhead (p: Parser<'a, 's>) : Parser<'a, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok (r, _, _) -> Ok (r, s, state)
      | Error _ as x -> x

  let inline ( <?> ) (p: Parser<'a, 's>) (label: string) : Parser<'a, 's> =
    fun (state, s) ->
      match run p state s with
      | Ok _ as x -> x
      | Error (_, state) -> err1 s.pos (Expected label) state

  let inline fail (msg: string) : Parser<'a, 's> = fun (state, s) -> err1 s.pos (Message msg) state

  let inline tuple2 (p1: Parser<'a, 's>) (p2: Parser<'b, 's>) : Parser<'a * 'b, 's> = p1 .>>. p2
  let inline tuple3 p1 p2 p3 : Parser<'a * 'b * 'c, 's> =
    pipe3 p1 p2 p3 (fun r1 r2 r3 -> r1,r2,r3)
  let inline tuple4 p1 p2 p3 p4 : Parser<'a * 'b * 'c * 'd, 's> =
    pipe4 p1 p2 p3 p4 (fun r1 r2 r3 r4 -> r1,r2,r3,r4)
  let inline tuple5 p1 p2 p3 p4 p5 : Parser<'a * 'b * 'c * 'd * 'e, 's> =
    pipe5 p1 p2 p3 p4 p5 (fun r1 r2 r3 r4 r5 -> r1,r2,r3,r4,r5)

  let inline parray (len: int) (p: Parser<'a, 's>) : Parser<'a[], 's> =
    fun (state, s) ->
      let result = Array.zeroCreate len
      let rec go i (state, s) =
        if i = len then Ok (result, s, state)
        else
          match run p state s with
          | Error e -> Error e
          | Ok (r, s, state) ->
            result.[i] <- r
            go (i+1) (state, s)
      go 0 (state, s)

  let inline skipArray (len: int) (p: Parser<'a, 's>) : Parser<unit, 's> =
    let rec go i (state, s) =
      if i = len then Ok ((), s, state)
      else
        match run p state s with
        | Error e -> Error e
        | Ok (_, s, state) -> go (i+1) (state, s)
    go 0

  [<Sealed>]
  type Inline =
    static member inline Many(stateFromFirstElement: 'a -> 'State,
                              foldState: 'State -> 'a -> 'State,
                              resultFromState: 'State -> 'r,
                              elementParser: Parser<'a, 's>,
                              ?firstElementParser: Parser<'a, 's>,
                              ?resultForEmptySequence: unit -> 'r) : Parser<'r, 's> =
      let p = elementParser
      let fp = defaultArg firstElementParser p
      let rec go acc (state, s) =
        match run p state s with
        | Error _ -> Ok (acc |> resultFromState, s, state)
        | Ok (r, s, state) -> go (foldState acc r) (state, s)
      fun (state, s) ->
        match run fp state s with
        | Error e ->
          match resultForEmptySequence with
          | Some _ -> Ok ((match resultForEmptySequence with Some f -> f () | None -> Unchecked.defaultof<_>), s, state)
          | None   -> Error e
        | Ok (r, s, state) ->
          let r = stateFromFirstElement r
          go r (state, s)

    static member inline SepBy(stateFromFirstElement: 'a -> 'State,
                               foldState: 'State -> 'b -> 'a -> 'State,
                               resultFromState: 'State -> 'r,
                               elementParser: Parser<'a, 's>,
                               separatorParser: Parser<'b, 's>,
                               ?firstElementParser: Parser<'a, 's>,
                               ?resultForEmptySequence: unit -> 'r,
                               ?separatorMayEndSequence: bool) : Parser<'r, 's> =
      let p = elementParser
      let fp = defaultArg firstElementParser p
      let sep = separatorParser
      let rec go acc (state, s) =
        match run sep state s with
        | Error _ -> Ok (acc |> resultFromState, s, state)
        | Ok (rSep, s, state) ->
          match run p state s with
          | Error e ->
            if defaultArg separatorMayEndSequence false then
              Ok (acc |> resultFromState, s, state)
            else Error e
          | Ok (r, s, state) -> go (foldState acc rSep r) (state, s)
      fun (state, s) ->
        match run fp state s with
        | Error e ->
          match resultForEmptySequence with
          | Some _ -> Ok ((match resultForEmptySequence with Some f -> f () | None -> Unchecked.defaultof<_>), s, state)
          | None   -> Error e
        | Ok (r, s, state) ->
          let r = stateFromFirstElement r
          go r (state, s)

    static member inline ManyTill(stateFromFirstElement: 'a -> 'State,
                                  foldState: 'State -> 'a -> 'State,
                                  resultFromStateAndEnd: 'State -> 'e -> 'r,
                                  elementParser: Parser<'a, 's>,
                                  endParser: Parser<'e, 's>,
                                  ?firstElementParser: Parser<'a, 's>,
                                  ?resultForEmptySequence: 'e -> 'r) : Parser<'r, 's> =
      let p = elementParser
      let fp = defaultArg firstElementParser p
      let ep = endParser
      let rec go acc (state, s) =
        match run ep state s with
        | Ok (rEnd, s, state) -> Ok (resultFromStateAndEnd acc rEnd, s, state)
        | Error _ ->
          match run p state s with
          | Ok (r, s, state) -> go (foldState acc r) (state, s)
          | Error e -> Error e
      fun (state, s) ->
        match resultForEmptySequence with
        | None ->
          match run fp state s with
          | Error e -> Error e
          | Ok (r, s, state) ->
            let r = stateFromFirstElement r
            go r (state, s)
        | Some _ ->
          match run fp state s with
          | Error _ ->
            match run ep state s with
            | Ok (rEnd, s, state) ->
              Ok ((match resultForEmptySequence with Some f -> f rEnd | None -> Unchecked.defaultof<_>), s, state)
            | Error e -> Error e
          | Ok (r, s, state) ->
            let r = stateFromFirstElement r
            go r (state, s)

  let many      p = Inline.Many((fun x -> [x]), (fun xs x -> x::xs), List.rev, p, resultForEmptySequence = fun () -> [])
  let many1     p = Inline.Many((fun x -> [x]), (fun xs x -> x::xs), List.rev, p)

  let skipMany  p = Inline.Many((fun _ -> ()), (fun _ _ -> ()), (fun xs -> xs), p, resultForEmptySequence = fun () -> ())
  let skipMany1 p = Inline.Many((fun _ -> ()), (fun _ _ -> ()), (fun xs -> xs), p)

  let sepBy         p sep = Inline.SepBy((fun x -> [x]), (fun xs _ x -> x::xs), List.rev,       p, sep, resultForEmptySequence = fun () -> [])
  let sepBy1        p sep = Inline.SepBy((fun x -> [x]), (fun xs _ x -> x::xs), List.rev,       p, sep)

  let skipSepBy     p sep = Inline.SepBy((fun _ -> ()),  (fun _ _ _ -> ()),     (fun xs -> xs), p, sep, resultForEmptySequence = fun () -> ())
  let skipSepBy1    p sep = Inline.SepBy((fun _ -> ()),  (fun _ _ _ -> ()),     (fun xs -> xs), p, sep)

  let sepEndBy      p sep = Inline.SepBy((fun x -> [x]), (fun xs _ x -> x::xs), List.rev,       p, sep, separatorMayEndSequence = true, resultForEmptySequence = fun () -> [])
  let sepEndBy1     p sep = Inline.SepBy((fun x -> [x]), (fun xs _ x -> x::xs), List.rev,       p, sep, separatorMayEndSequence = true)

  let skipSepEndBy  p sep = Inline.SepBy((fun _ -> ()),  (fun _ _ _ -> ()),     (fun xs -> xs), p, sep, separatorMayEndSequence = true, resultForEmptySequence = fun () -> ())
  let skipSepEndBy1 p sep = Inline.SepBy((fun _ -> ()),  (fun _ _ _ -> ()),     (fun xs -> xs), p, sep, separatorMayEndSequence = true)

  let manyTill       p endp = Inline.ManyTill((fun x -> [x]), (fun xs x -> x::xs), (fun xs _ -> List.rev xs), p, endp, resultForEmptySequence = fun _ -> [])
  let many1Till      p endp = Inline.ManyTill((fun x -> [x]), (fun xs x -> x::xs), (fun xs _ -> List.rev xs), p, endp)

  let skipManyTill   p endp = Inline.ManyTill((fun _ -> ()),  (fun _ _ -> ()),     (fun _ _ -> ()), p, endp, resultForEmptySequence = fun _ -> ())
  let skipMany1Till  p endp = Inline.ManyTill((fun _ -> ()),  (fun _ _ -> ()),     (fun _ _ -> ()), p, endp)

  let chainl1 p op =
      Inline.SepBy((fun x0 -> x0), (fun x f y -> f x y), (fun x -> x), p, op)

  let chainl p op x = chainl1 p op <|>% x

  let chainr1 p op =
      Inline.SepBy(elementParser = p, separatorParser = op,
                   stateFromFirstElement = (fun x0 -> [(Unchecked.defaultof<_>, x0)]),
                   foldState = (fun acc op x -> (op, x)::acc),
                   resultFromState = function // is called with (op, y) list in reverse order
                                     | ((op, y)::tl) ->
                                         let rec calc op y lst =
                                             match lst with
                                             | (op2, x)::tl -> calc op2 (op x y) tl
                                             | [] -> y // op is null
                                         calc op y tl
                                     | [] -> // shouldn't happen
                                             failwith "chainr1")

  let inline createParserForwardedToRef () : Parser<'a, 'u> * Parser<'a, 'u> ref =
    let dummy (_, _) = failwith "invalid definition with createParserForwardedToRef"
    let r = ref dummy
    (fun (state, s) -> !r (state, s)), r

  let inline getUserState (state, s) = Ok (state, s, state)
  let inline setUserState state : Parser<unit, 'State> = fun (_, s) -> Ok ((), s, state)
  let inline updateUserState f : Parser<unit, 'State> = fun (state, s) -> Ok ((), s, f state)
  let inline userStateSatisfies (cond: 's -> bool) : Parser<unit, 's> =
    fun (state, s) ->
      if cond state then Ok ((), s, state)
      else err1 s.pos (Message "userStateSatisfies failed") state
  let inline getPosition (state, s: StringSegment) = Ok (s.pos, s, state)

  [<Sealed>]
  type ParserCombinator() =
    member inline __.Delay(f)   = fun state -> (f ()) state
    member inline __.Return(x)  = preturn x
    member inline __.Bind(p, f) = p >>= f
    member inline __.Zero()     = pzero
    member inline __.ReturnFrom(p) = p
    member inline __.TryWith(p, cf) =
      fun state -> try p state with e -> (cf e) state
    member inline __.TryFinally(p, ff) =
      fun state -> try p state finally ff ()

  let parse = ParserCombinator()

open Primitives

/// MiniParsec does not support fatal errors.
/// every MiniParsec's function backtracks by default.
module FParsecCompat =
  let inline attempt (p: Parser<_, _>) = p
  let inline ( >>=? ) p1 p2 = p1 >>= p2
  let inline ( >>?  ) p1 p2 = p1 >>. p2
  let inline ( .>>? ) p1 p2 = p1 .>> p2
  let inline ( .>>.? ) p1 p2 = p1 .>>. p2
  let inline ( <??> ) p msg = p <?> msg
  let inline failFatally s = fail s

[<AutoOpen>]
module CharParsers =
  open StringSegment

  let inline errN pos xs state = Error ([pos, xs], state)

  let inline charReturn c v : Parser<'a, _> =
    fun (state, s) ->
      match getSafe 0 s with
      | EOS -> errN s.pos [Expected (string c |> quote); Unexpected "EOF"] state
      | head ->
        if head = c then Ok (v, s |> skip 1, state)
        else errN s.pos [Expected (string c |> quote); Unexpected (string head |> quote)] state
  let inline pchar c = charReturn c c
  let inline skipChar c = charReturn c ()

  let anyChar : Parser<char, 's> =
    fun (state, s) ->
      match getSafe 0 s with
      | EOS -> errN s.pos [Expected "any char"; Unexpected "EOF"] state
      | c   -> Ok (c, s |> skip 1, state)
  let skipAnyChar : Parser<unit, 's> =
    fun (state, s) ->
      match getSafe 0 s with
      | EOS -> errN s.pos [Expected "any char"; Unexpected "EOF"] state
      | _   -> Ok ((), s |> skip 1, state)

  type CharSet = System.Collections.Generic.HashSet<char>

  let inline satisfyL (cond: char -> bool) (label: string) : Parser<char, _> =
    fun (state, s) ->
      match getSafe 0 s with
      | EOS -> errN s.pos [Expected label; Unexpected "EOF"] state
      | c ->
        if cond c then Ok (c, s |> skip 1, state)
        else
          errN s.pos [Expected label; Unexpected (string c)] state
  let inline skipSatisfyL cond label : Parser<unit, _> = satisfyL cond label >>% ()

  let inline satisfy cond : Parser<char, _> = satisfyL cond "a char with condition"
  let inline skipSatisfy cond : Parser<unit, _> = skipSatisfyL cond "a char with condition"

  let inline anyOf (chars: char seq) : Parser<char, _> =
    let set = CharSet(chars)
    satisfyL set.Contains (sprintf "one of %A" (Seq.toList chars))
  let inline skipAnyOf (chars: char seq) : Parser<unit, _> =
    let set = CharSet(chars)
    skipSatisfyL set.Contains (sprintf "one of %A" (Seq.toList chars))
  let inline noneOf (chars: char seq) : Parser<char, _> =
    let set = CharSet(chars)
    satisfyL (set.Contains >> not) (sprintf "one of %A" (Seq.toList chars))
  let inline skipNoneOf (chars: char seq) : Parser<unit, _> =
    let set = CharSet(chars)
    skipSatisfyL (set.Contains >> not) (sprintf "one of %A" (Seq.toList chars))

  open System

  let inline asciiLower (state, s) = satisfyL (fun c -> 'a' <= c && c <= 'z') ("[a-z]") (state, s)
  let inline asciiUpper (state, s) = satisfyL (fun c -> 'A' <= c && c <= 'Z') ("[A-Z]") (state, s)
  let inline asciiLetter (state, s) = satisfyL (fun c -> ('A' <= c && c <= 'Z') || ('a' <= c && c <= 'z')) ("[a-zA-Z]") (state, s)
  let inline lower (state, s) = satisfyL Char.IsLower ("Lowercase Letter") (state, s)
  let inline upper (state, s) = satisfyL Char.IsUpper ("Uppercase Letter") (state, s)
  let inline letter (state, s) = satisfyL Char.IsLetter ("Letter") (state, s)
  let inline digit (state, s) = satisfyL Char.IsDigit ("[0-9]") (state, s)
  let inline hex (state, s) = satisfyL (fun c -> Char.IsDigit c || ('A' <= c && c <= 'F') || ('a' <= c && c <= 'f')) ("[0-9a-fA-F]") (state, s)
  let inline octal (state, s) = satisfyL (fun c -> '0' <= c && c <= '7') ("[0-7]") (state, s)
  let inline tab (state, s) = pchar '\t' (state, s)

  let inline isAnyOf (chars: char seq) = let set = CharSet(chars) in fun c -> set.Contains c
  let inline isNoneOf (chars: char seq) = let set = CharSet(chars) in fun c -> set.Contains c |> not
  let inline isAsciiUpper (c: char) = 'A' <= c && c <= 'Z'
  let inline isAsciiLower (c: char) = 'a' <= c && c <= 'z'
  let inline isAsciiLetter (c: char) = isAsciiLower c || isAsciiUpper c
  let inline isUpper (c: char) = Char.IsUpper c
  let inline isLower (c: char) = Char.IsLower c
  let inline isLetter (c: char) = Char.IsLetter c
  let inline isDigit (c: char) = Char.IsDigit c
  let inline isHex (c: char) = Char.IsDigit c || ('A' <= c && c <= 'F') || ('a' <= c && c <= 'f')
  let inline isOctal (c: char) = '0' <= c && c <= '7'

  let inline newlineReturn v : Parser<'a, _> =
    fun (state, s) ->
      match getSafe 0 s with
      | '\n' -> Ok (v, s |> skip 1, state)
      | _ -> err1 s.pos (Expected "newline") state
  let inline newline (state, s) = newlineReturn '\n' (state, s)
  let inline skipNewline (state, s) = newlineReturn () (state, s)

  let spaces : Parser<unit, 's> =
    let rec go (state, s: StringSegment) =
      match getSafe 0 s with
      | '\n' | '\t' | ' ' -> go (state, s |> skip 1)
      | _ -> Ok ((), s, state)
    go
  let spaces1 : Parser<unit, 's> =
    let rec go (state, s: StringSegment) =
      match getSafe 0 s with
      | '\n' | '\t' | ' ' -> go (state, s |> skip 1)
      | _ -> Ok ((), s, state)
    fun (state, s) ->
      match getSafe 0 s with
      | '\n' | '\t' | ' ' -> go (state, s |> skip 1)
      | _ -> err1 s.pos (Expected "one or more spaces") state

  let eof : Parser<unit, 's> =
    fun (state, s) ->
      match getSafe 0 s with
      | EOS -> Ok ((), s, state)
      | _ -> err1 s.pos (Expected "EOF") state

  let inline stringReturn (str: string) v : Parser<'a, 's> =
    fun (state, s) ->
      if s |> startsWith str then
        Ok (v, s |> skip str.Length, state)
      else
        err1 s.pos (Expected (quote str)) state
  let inline pstring str : Parser<string, 's> = stringReturn str str
  let inline skipString str : Parser<unit, 's> = stringReturn str ()

  let inline anyString (len: int) : Parser<string, 's> =
    fun (state, s) ->
      if s.length >= len then
        Ok (s.[0..len-1] |> toString, s |> skip len, state)
      else
        err1 s.pos (Unexpected "EOF") state

  let inline skipAnyString (len: int) : Parser<unit, 's> =
    fun (state, s) ->
      if s.length >= len then
        Ok ((), s |> skip len, state)
      else
        err1 s.pos (Unexpected "EOF") state

  let inline restOfLine (skipNewLine: bool) : Parser<string, 's> =
    let rec go i state (s: StringSegment) =
      match getSafe i s with
      | EOS | '\n' ->
        let str = s.[0..i-1] |> toString
        Ok (str, s |> skip (if skipNewLine then i+1 else i), state)
      | _ -> go (i+1) state s
    fun (state, s) -> go 0 state s

  let inline skipRestOfLine (skipNewLine: bool) : Parser<unit, 's> =
    let rec go i state (s: StringSegment) =
      match getSafe i s with
      | EOS | '\n' ->
        Ok ((), s |> skip (if skipNewLine then i+1 else i), state)
      | _ -> go (i+1) state s
    fun (state, s) -> go 0 state s

  let inline charsTillString str (skipString: bool) : Parser<string, 's> =
    fun (state, s) ->
      let index = s |> indexOfSequence str
      if index < 0 then err1 (s |> skip s.length).pos (Expected (quote str)) state
      else
        let res = s.[0..index-1] |> toString
        let nextIndex = if skipString then index + str.Length else index
        Ok (res, s |> skip nextIndex, state)

  let inline skipCharsTillString str (skipString: bool) : Parser<unit, 's> =
    fun (state, s) ->
      let index = s |> indexOfSequence str
      if index < 0 then err1 (s |> skip s.length).pos (Expected (quote str)) state
      else
        let nextIndex = if skipString then index + str.Length else index
        Ok ((), s |> skip nextIndex, state)

  let inline internal manySatisfyImpl failOnZero cond1 cond label : Parser<string, 's> =
    let rec go i (state, s) =
      let c = getSafe i s
      if i = 0 then
        if cond1 c then go (i+1) (state, s)
        else if failOnZero then err1 s.pos (Expected label) state
        else Ok ("", s, state)
      else
        if cond c then go (i+1) (state, s)
        else Ok (s.[0..i-1] |> toString, s |> skip i, state)
    go 0
  let inline internal skipManySatisfyImpl failOnZero cond1 cond label : Parser<unit, 's> =
    let rec go i (state, s) =
      let c = getSafe i s
      if i = 0 then
        if cond1 c then go (i+1) (state, s)
        else if failOnZero then err1 s.pos (Expected label) state
        else Ok ((), s, state)
      else
        if cond c then go (i+1) (state, s)
        else Ok ((), s |> skip i, state)
    go 0

  let inline manySatisfy cond = manySatisfyImpl false cond cond "a char satisfying the condition"
  let inline many1Satisfy cond = manySatisfyImpl true cond cond "a char satisfying the condition"
  let inline many1SatisfyL cond label = manySatisfyImpl true cond cond label
  let inline skipManySatisfy cond = skipManySatisfyImpl false cond cond "a char satisfying the condition"
  let inline skipMany1Satisfy cond = skipManySatisfyImpl true cond cond "a char satisfying the condition"
  let inline skipMany1SatisfyL cond label = skipManySatisfyImpl true cond cond label
  let inline manySatisfy2 cond1 cond = manySatisfyImpl false cond1 cond "a char satisfying the condition"
  let inline many1Satisfy2 cond1 cond = manySatisfyImpl true cond1 cond "a char satisfying the condition"
  let inline many1Satisfy2L cond1 cond label = manySatisfyImpl true cond1 cond label
  let inline skipManySatisfy2 cond1 cond = skipManySatisfyImpl false cond1 cond "a char satisfying the condition"
  let inline skipMany1Satisfy2 cond1 cond = skipManySatisfyImpl true cond1 cond "a char satisfying the condition"
  let inline skipMany1Satisfy2L cond1 cond label = skipManySatisfyImpl true cond1 cond label

  let inline internal manyMinMaxSatisfyImpl min max cond1 cond label : Parser<string, 's> =
    if max < 0 then System.ArgumentOutOfRangeException("max", "max is negative") |> raise
    let rec go i (state, s) =
      let c = getSafe i s
      if i < max && (if i = 0 then cond1 c else cond c) then go (i+1) (state, s)
      else if min <= i then Ok (s.[0..i-1] |> toString, s |> skip i, state)
      else err1 s.pos (Expected label) state
    go 0
  let inline internal skipManyMinMaxSatisfyImpl min max cond1 cond label : Parser<unit, 's> =
    if max < 0 then System.ArgumentOutOfRangeException("max", "max is negative") |> raise
    let rec go i (state, s) =
      let c = getSafe i s
      if i < max && (if i = 0 then cond1 c else cond c) then go (i+1) (state, s)
      else if min <= i then Ok ((), s |> skip i, state)
      else err1 s.pos (Expected label) state
    go 0

  let inline manyMinMaxSatisfy min max cond = manyMinMaxSatisfyImpl min max cond cond "a char satisfying the condition"
  let inline manyMinMaxSatisfyL min max cond label = manyMinMaxSatisfyImpl min max cond cond label
  let inline manyMinMaxSatisfy2 min max cond cond2 = manyMinMaxSatisfyImpl min max cond cond2 "a char satisfying the condition"
  let inline manyMinMaxSatisfy2L min max cond cond2 label = manyMinMaxSatisfyImpl min max cond cond2 label
  let inline skipManyMinMaxSatisfy min max cond = skipManyMinMaxSatisfyImpl min max cond cond "a char satisfying the condition"
  let inline skipManyMinMaxSatisfyL min max cond label = skipManyMinMaxSatisfyImpl min max cond cond label
  let inline skipManyMinMaxSatisfy2 min max cond cond2 = skipManyMinMaxSatisfyImpl min max cond cond2 "a char satisfying the condition"
  let inline skipManyMinMaxSatisfy2L min max cond cond2 label = skipManyMinMaxSatisfyImpl min max cond cond2 label

  open System.Text.RegularExpressions
  let inline internal regexImpl compiled pattern label : Parser<string, 's> =
    let opt =
      let o = RegexOptions.ECMAScript ||| RegexOptions.Multiline
      if compiled then o ||| RegexOptions.Compiled else o
    let regex = Regex("\\A" + pattern, opt)
    fun (state, s) ->
      let str = s.Value
      let m = regex.Match str
      if m.Success then
        Ok (m.Value, s |> skip m.Length, state)
      else
        err1 s.pos (Expected label) state
  
  let inline regex pattern = regexImpl false pattern pattern
  let inline regexL pattern label = regexImpl false pattern label
  let inline regexCompiled pattern = regexImpl true pattern pattern
  let inline regexCompiledL pattern label = regexImpl true pattern label

  // no identifier support

  #if FABLE_COMPILER
  type StringBuilder (s: string) =
    let mutable s = s
    new () = StringBuilder ("")
    member __.Length = s.Length
    member sb.Append (s': string) =
      s <- s + s'; sb
    member inline sb.Append (c: char) = sb.Append (string c)
    member inline sb.Append (num: ^n) = sb.Append (sprintf "%d" num)
    member inline sb.Append (o: obj) = sb.Append (string o)
    member inline sb.AppendLine () = sb.Append System.Environment.NewLine
    member inline sb.AppendLine (s: string) =
      (sb.Append (s)).AppendLine ()
    member sb.Remove (startIndex: int, length: int) =
      if startIndex + length >= s.Length
      then s <- s.Substring (0, startIndex)
      else s <- s.Substring (0, startIndex) + s.Substring (startIndex + length)
      sb
    member inline __.ToString (startIndex: int, length: int) =
      s.Substring (startIndex, length)
    override __.ToString() = s
  #else
  type StringBuilder = System.Text.StringBuilder
  #endif
  
  let inline internal manyCharsImpl failOnZero (p1: Parser<char, _>) (p: Parser<char, _>) : Parser<string, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> if failOnZero then Error e else Ok ("", s, state)
      | Ok (c1, s, state) ->
        let sb = StringBuilder()
        sb.Append c1 |> ignore
        let rec go (state, s) =
          match run p state s with
          | Error _ -> Ok (sb.ToString(), s, state)
          | Ok (c, s, state) -> sb.Append c |> ignore; go (state, s)
        go (state, s)

  let inline manyChars p = manyCharsImpl false p p
  let inline many1Chars p = manyCharsImpl true p p
  let inline manyChars2 p1 p = manyCharsImpl false p1 p
  let inline many1Chars2 p1 p = manyCharsImpl true p1 p

  let inline internal manyCharsTillApplyImpl
    failOnZero (p1: Parser<char, _>) (p: Parser<char, _>) (till: Parser<'a, _>) (f: string -> 'a -> 'b) : Parser<'b, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e ->
        if failOnZero then Error e
        else
          match run till state s with
          | Ok (a, s, state) -> Ok (f "" a, s, state)
          | Error e -> Error e
      | Ok (c1, s, state) ->
        let sb = StringBuilder()
        sb.Append c1 |> ignore
        let rec go (state, s) =
          match run till state s with
          | Ok (a, s, state) -> Ok (f (sb.ToString()) a, s, state)
          | Error _ ->
            match run p state s with
            | Error e -> Error e
            | Ok (c, s, state) -> sb.Append c |> ignore; go (state, s)
        go (state, s)

  let inline manyCharsTill p till = manyCharsTillApplyImpl false p p till (fun s _ -> s)
  let inline manyCharsTill2 p1 p till = manyCharsTillApplyImpl false p1 p till (fun s _ -> s)
  let inline manyCharsTillApply p till f = manyCharsTillApplyImpl false p p till f
  let inline manyCharsTillApply1 p1 p till f = manyCharsTillApplyImpl false p1 p till f
  let inline many1CharsTill p till = manyCharsTillApplyImpl true p p till (fun s _ -> s)
  let inline many1CharsTill2 p1 p till = manyCharsTillApplyImpl true p1 p till (fun s _ -> s)
  let inline many1CharsTillApply p till f = manyCharsTillApplyImpl true p p till f
  let inline many1CharsTillApply1 p1 p till f = manyCharsTillApplyImpl true p1 p till f

  let inline internal manyStringsImpl failOnZero (p1: Parser<string, _>) (p: Parser<string, _>) : Parser<string, 's> =
    fun (state, s) ->
      match run p1 state s with
      | Error e -> if failOnZero then Error e else Ok ("", s, state)
      | Ok (c1, s, state) ->
        let sb = StringBuilder(c1)
        let rec go (state, s) =
          match run p state s with
          | Error _ -> Ok (sb.ToString(), s, state)
          | Ok (c, s, state) -> sb.Append c |> ignore; go (state, s)
        go (state, s)

  let inline manyStrings p = manyStringsImpl false p p
  let inline many1Strings p = manyStringsImpl true p p
  let inline manyStrings2 p1 p = manyStringsImpl false p1 p
  let inline many1Strings2 p1 p = manyStringsImpl true p1 p

  let inline internal stringsSepByImpl failOnZero (p: Parser<string, _>) (sep: Parser<string, _>) : Parser<string, _> =
    fun (state, s) ->
      match run p state s with
      | Error e -> if failOnZero then Error e else Ok ("", s, state)
      | Ok (c1, s, state) ->
        let sb = StringBuilder(c1)
        let rec go (state, s) =
          match run sep state s with
          | Error _ -> Ok (sb.ToString(), s, state)
          | Ok (csep, s, state) ->
            match run p state s with
            | Ok (c, s, state) ->
              sb.Append csep |> ignore
              sb.Append c |> ignore
              go (state, s)
            | Error e -> Error e
        go (state, s)

  let inline stringsSepBy p sep = stringsSepByImpl false p sep
  let inline stringsSepBy1 p sep = stringsSepByImpl true p sep

  let inline skipped (p: Parser<unit, 's>) : Parser<string, 's> =
    fun (state, s) ->
      match run p state s with
      | Error e -> Error e
      | Ok (_, s', state) ->
        let str = s.[0..s'.startIndex-s.startIndex-1] |> toString
        Ok (str, s', state)

  let inline withSkippedString (f: string -> 'a -> 'b) (p: Parser<'a, _>) : Parser<'b, _> =
    fun (state, s) ->
      match run p state s with
      | Error e -> Error e
      | Ok (a, s', state) ->
        let str = s.[0..s'.startIndex-s.startIndex-1] |> toString
        Ok (f str a, s', state)

  module Internal =
    let pfloatUnit : Parser<string, unit> =
      choiceL [
        stringReturn "NaN" "NaN";
        stringReturn "Inf" "Infinity" .>> optional (skipString "inity");
        pipe4
          (opt (charReturn '+' "" <|> charReturn '-' "-") |>> Option.defaultValue "")
          (many1Chars digit)
          (skipped (skipChar '.' >>. skipManySatisfy isDigit))
          (skipped (optional (skipSatisfy (function 'e'|'E'->true|_->false) >>.
                              (optional (skipSatisfy (function '+'|'-'->true|_->false)))
                              >>. skipMany1Satisfy isDigit)))
          (fun a b c d -> a + b + c + d)
      ] "float"

    type Adic = int

    let pIntLikeUnit : Parser<bool * Adic * string, unit> =
      tuple2
        (opt <| satisfy (function '+'|'-' -> true | _ -> false))
        (choice [
          (skipString "0x" <|> skipString "0X") >>. many1Chars hex
            |>> fun x -> 16, x
          (skipString "0o" <|> skipString "0O") >>. many1Chars octal
            |>> fun x -> 8, x
          (skipString "0b" <|> skipString "0B") >>. many1Chars (satisfy (function '0'|'1'->true|_->false))
            |>> fun x -> 2, x
          many1Chars digit |>> fun x -> 10, x
        ])
        |>> (function | None, (style, s) | Some '+', (style, s) -> true,style,s 
                      | Some _, (style, s) -> false,style,s)
        <?> "integer"

    let pUIntLikeUnit : Parser<Adic * string, unit> =
        choiceL [
          (skipString "0x" <|> skipString "0X") >>. many1Chars hex
            |>> fun x -> 16, x
          (skipString "0o" <|> skipString "0O") >>. many1Chars octal
            |>> fun x -> 8, x
          (skipString "0b" <|> skipString "0B") >>. many1Chars (satisfy (function '0'|'1'->true|_->false))
            |>> fun x -> 2, x
          many1Chars digit |>> fun x -> 10, x
        ] "unsigned integer"

  let pfloat : Parser<float, 's> =
    fun (state, s) ->
      match run Internal.pfloatUnit () s with
      | Error (es, ()) -> Error (es, state)
      | Ok (x, s, ()) ->
        try Ok (float x, s, state)
        with :? OverflowException -> err1 s.pos (Message "value was too large and too small.") state

  let inline private sign isPositive x = if isPositive then x else -x
  let inline private pint (conv: string * int -> 'I) (state, s) =
    match run Internal.pIntLikeUnit () s with
    | Error (es, ()) -> Error (es, state)
    | Ok ((positive, adic, str), s, ()) ->
      try Ok (conv(str, adic) |> sign positive, s, state)
      with :? OverflowException -> err1 s.pos (Message "value was too large and too small.") state
  let inline private puint (conv: string * int -> 'I) (state, s) =
    match run Internal.pUIntLikeUnit () s with
    | Error (es, ()) -> Error (es, state)
    | Ok ((adic, str), s, ()) ->
      try Ok (conv(str, adic), s, state)
      with :? OverflowException -> err1 s.pos (Message "value was too large and too small.") state

  let pint8   : Parser<int8, 's>   = fun x -> pint  Convert.ToSByte x
  let puint8  : Parser<uint8, 's>  = fun x -> puint Convert.ToByte x
  let pint16  : Parser<int16, 's>  = fun x -> pint  Convert.ToInt16 x
  let puint16 : Parser<uint16, 's> = fun x -> puint Convert.ToUInt16 x
  let pint32  : Parser<int, 's>    = fun x -> pint  Convert.ToInt32 x
  let puint32 : Parser<uint32, 's> = fun x -> puint Convert.ToUInt32 x
  let pint64  : Parser<int64, 's>  = fun x -> pint  Convert.ToInt64 x
  let puint64 : Parser<uint64, 's> = fun x -> puint Convert.ToUInt64 x

  let notFollowedByEof : Parser<unit, 's> =
    fun (state, s) ->
      if getSafe 0 s <> EOS then Ok ((), s, state)
      else err1 s.pos (Unexpected "EOF") state
  let followedByNewline : Parser<unit, 's> =
    fun (state, s) ->
      if getSafe 0 s = '\n' then Ok ((), s, state)
      else err1 s.pos (Expected "newline") state
  let notFollowedByNewline : Parser<unit, 's> =
    fun (state, s) ->
      if getSafe 0 s <> '\n' then Ok ((), s, state)
      else err1 s.pos (Unexpected "newline") state
  let inline followedByString str : Parser<unit, 's> =
    fun (state, s) ->
      if s |> startsWith str then Ok ((), s, state)
      else err1 s.pos (Expected str) state
  let inline notFollowedByString str : Parser<unit, 's> =
    fun (state, s) ->
      if s |> startsWith str |> not then Ok ((), s, state)
      else err1 s.pos (Unexpected str) state

  let inline nextCharSatisfies (cond: char -> bool) : Parser<unit, 's> =
    fun (state, s) ->
      let c = getSafe 0 s
      if c <> EOS && cond c then Ok ((), s, state)
      else err1 s.pos (Expected "a char satisfying the condition.") state
  let inline nextCharSatisfiesNot cond = nextCharSatisfies (cond >> not)
  let inline next2CharsSatisfy (cond: char -> char -> bool) : Parser<unit, 's> =
    fun (state, s) ->
      match getSafe 0 s, getSafe 1 s with
      | EOS, _ -> err1 s.pos (Unexpected "EOF") state
      | _, EOS -> err1 (s |> skip 1).pos (Unexpected "EOF") state
      | c1, c2 when cond c1 c2 -> Ok ((), s, state)
      | c1, c2   -> errN s.pos [Expected "2 chars satisfying the condition"; Unexpected (sprintf "'%c%c'" c1 c2)] state
  let inline next2CharsSatisfyNot cond = next2CharsSatisfy (fun c1 c2 -> not (cond c1 c2))
  let inline previousCharSatisfies (cond: char -> bool) : Parser<unit, 's> =
    fun (state, s) ->
      match s.GetSafeOverOriginal -1 with
      | EOS -> err1 s.pos (Unexpected "start of input") state
      | c when cond c -> Ok ((), s, state)
      | _ -> err1 s.pos (Expected "previous char satisfying the condition") state
  let previousCharSatisfiesNot cond = previousCharSatisfies (cond >> not)

open CharParsers

module Extensions =
  module Convert =
    let inline private foldi folder state xs =
      Seq.fold (fun (i, state) x -> (i + 1, folder i state x)) (0, state) xs |> snd
    let inline hexsToInt (hexs: #seq<char>) =
      let len = Seq.length hexs - 1
      hexs |> foldi (fun i sum x ->
        let n =
          let n = int x - int '0'
          if n < 10 then n
          else if n < 23 then n - 7
          else n - 44
        sum + n * pown 16 (len - i)) 0
    let inline digitsToInt (digits: #seq<char>) =
      let len = Seq.length digits - 1
      digits |> foldi (fun i sum x ->
        sum + (int x - int '0') * pown 10 (len - i)) 0

  /// Variant of `<|>` but accept different types of parsers and returns `Choice<'a, 'b>`.
  let inline (<||>) a b = (a |>> Choice1Of2) <|> (b |>> Choice2Of2)

  /// short hand for `skipString s .>> spaces`
  let inline syn s = skipString s

  /// short hand for `skipChar c `
  let inline cyn c = skipChar c

  /// short hand for `x .>> spaces`
  let inline ws x = x .>> spaces

  /// Given a sequence of `(key, value)`, parses the string `key`
  /// and returns the corresponding `value`.
  let inline pdict (d: list<_*_>) =
    d |> List.map (fun (k, v) -> pstring k >>% v) |> choice

  /// Optimized version of `pdict d <?> descr`.
  let inline pdictL (d: list<_*_>) descr =
    d |> List.map (fun (k, v) -> pstring k >>% v) |> choiceL <| descr

  /// String with escaped characters. Should be used along with `between`.
  let inline escapedString (escapedChars: #seq<char>) =
    let controls =
      pdictL [
        "\\b", '\b'; "\\t", '\t'; "\\n", '\n';
        "\\v", '\u000B'; "\\f", '\u000C'; "\\r", '\r'; "\\\\", '\\'
      ] "control characters"
    let unicode16bit =
      syn "\\u" >>. parray 4 hex |>> (Convert.hexsToInt >> char)
    let unicode32bit =
      syn "\\U" >>. parray 8 hex |>> (Convert.hexsToInt >> char)
    let customEscapedChars =
      let d = escapedChars |> Seq.map (fun c -> sprintf "\\%c" c, c) |> Seq.toList
      pdict d
    
    let escape = choice [controls; unicode16bit; unicode32bit; customEscapedChars]
    let nonEscape = noneOf (sprintf "\\\b\t\n\u000B\u000C\r%s" (System.String.Concat escapedChars))
    let character = nonEscape <|> escape
    many character |>> System.String.Concat

  /// Defines a recursive rule.
  let inline recursive (definition: (Parser<'a, _> -> Parser<'a, _>)) =
    let p, pr = createParserForwardedToRef()
    pr := definition p
    p
