module Conference.Ws

open Conference.Types
open Server.ServerTypes
open Browser.WebSocket
open Browser.Types
open Thoth.Json
open Application

let inline private encode msg =
  Encode.Auto.toString(0, msg)

let private websocketNotConnected =
  fun _ -> failwith "WebSocket not connected"

let mutable private sendPerWebsocket : ClientMsg<Domain.Commands.Command,API.QueryParameter> -> unit =
  websocketNotConnected

let mutable closeWebsocket : unit -> unit =
  websocketNotConnected

let startWs token dispatch =
  let onMsg : MessageEvent -> obj =
    (fun (wsMsg : MessageEvent) ->
      let msg =
        Decode.Auto.unsafeFromString<ServerMsg<Domain.Events.Event,API.QueryResult>> <| unbox wsMsg.data

      Received msg |> dispatch

      null
    )

  let ws = WebSocket.Create("ws://127.0.0.1:8085" + Server.Urls.Conference + "?jwt=" + token)

  let send msg =
    ws.send (Encode.Auto.toString<ClientMsg<Domain.Commands.Command,API.QueryParameter>>(0, msg))

  ws.onopen <- (fun _ -> send Connect ; null)
  ws.onmessage <- onMsg
  ws.onerror <- (fun err -> printfn "%A" err ; null)

  sendPerWebsocket <- send

  closeWebsocket <- (fun _ -> sendPerWebsocket <- websocketNotConnected ; ws.close (1000,"") |> ignore )

  ()

let stopWs _ =
  closeWebsocket ()

  ()


let wsCmd cmd =
  [fun _ -> sendPerWebsocket cmd]

let transactionId() =
  EventSourced.TransactionId <| System.Guid.NewGuid()

let createQuery query =
//  { TODO think of QueryId
//    Query.Id = QueryId <| System.Guid.NewGuid()
//    Query.Parameter = query
//  }
  query
