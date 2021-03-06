module Domain.Behaviour

open Domain
open Model
open Commands
open Events
open Projections
open EventSourced

let (|OrganizerAlreadyInConference|_|) organizers organizer =
  match organizers |> List.contains organizer with
  | true -> Some organizer
  | false -> None

let (|OrganizerNotInConference|_|) organizers organizer =
  match organizers |> List.contains organizer with
  | false -> Some organizer
  | true -> None

let (|AlreadyVotedForAbstract|_|) votingResults voting =
  match List.contains voting votingResults with
  | true -> Some voting
  | false -> None

let (|DidNotVoteForAbstract|_|) votingResults voting =
  match not <| List.contains voting votingResults with
  | true -> Some voting
  | false -> None

let (|VoterIsNotAnOrganizer|_|) (organizers: Organizers) (voting : Voting) =
  let isNotOrganizer voting =
    organizers
    |> List.map (fun x -> x.Id)
    |> List.contains (extractVoterId voting)
    |> not

  match isNotOrganizer voting with
  | true -> Some voting
  | false -> None

let (|VotingisNotIssued|_|) (votings: Voting list) (voting : Voting) =
  let isIssued voting =
    votings |> List.contains voting

  match isIssued voting with
  | true -> None
  | false -> Some voting


let numberOfVotesExceeded votingResults getVote (voting : Voting) max =
  let number =
    votingResults
    |> List.filter (getVote <| extractVoterId voting)
    |> List.length

  match number >= max with
  | true -> Some voting
  | false -> None

let handleProposeAbstract proposed history =
  match (conferenceState history).CallForPapers with
  | Open ->
      [AbstractWasProposed proposed]

  | NotOpened ->
      [ProposingDenied "Call For Papers Not Opened" |> DomainError]

  | Closed ->
      [ProposingDenied "Call For Papers Closed" |> DomainError]

let score m (abstr : AbstractId) =
    match m |> Map.tryFind abstr with
    | Some value -> m |> Map.add abstr (value + 1)
    | None -> m |> Map.add abstr 1

let scoreAbstracts state =
  let talks,_ =
    state.Abstracts
    |> List.partition (fun abstr -> abstr.Type = Talk)

  let votes,vetos =
    state.Votings
    |> List.partition (function | Voting.Voting (_,_,Veto) -> false | _ -> true)

  let abstractsWithVetos =
    vetos
    |> List.map extractAbstractId

  let withoutVetos =
    votes
    |> List.map extractAbstractId
    |> List.filter (fun abstractId -> abstractsWithVetos |> List.contains abstractId |> not)

  let sumPoints abstractId =
    votes
      |> List.filter (fun voting -> voting |> extractAbstractId = abstractId)
      |> List.map extractPoints
      |> List.sumBy (function | Zero -> 0 | One -> 1 | Two -> 2 | Veto -> 0)

  let accepted =
    withoutVetos
    |> Seq.sortByDescending sumPoints
    |> Seq.distinct
    |> Seq.truncate state.AvailableSlotsForTalks
    |> Seq.toList

  let rejected =
    talks
    |> List.map (fun abstr -> abstr.Id)
    |> List.filter (fun id -> accepted |> List.contains id |> not)

  accepted
  |> List.map AbstractWasAccepted
  |> (@) (rejected |> List.map AbstractWasRejected)

let finishVotingPeriod conference =
  match conference.CallForPapers,conference.VotingPeriod with
  | Closed,InProgress ->
      let unfinishedVotings =
        conference.Abstracts
        |> Seq.map (fun abstr ->
            conference.Votings
            |> List.map extractAbstractId
            |> List.filter (fun id -> id = abstr.Id)
            |> List.length)
        |> Seq.filter (fun votes ->
            votes <> conference.Organizers.Length)
        |> Seq.length
      let events =
        match unfinishedVotings with
        | 0 ->
            [VotingPeriodWasFinished]
            |> (@) (scoreAbstracts conference)
        | _ -> [FinishingDenied "Not all abstracts have been voted for by all organisers" |> DomainError]
      events

  | Closed,Finished -> [FinishingDenied "Voting Period Already Finished" |> DomainError]

  | _,_ -> [FinishingDenied "Call For Papers Not Closed" |> DomainError]

let handleFinishVotingPeriod history =
  history
  |> conferenceState
  |> finishVotingPeriod

let reopenVotingPeriod conference =
  match conference.CallForPapers,conference.VotingPeriod with
  | Closed,Finished ->
      [VotingPeriodWasReopened]

  | _,_ ->
    [FinishingDenied "Call For Papers Not Closed" |> DomainError]

let handleReopenVotingPeriod history =
  history
  |> conferenceState
  |> reopenVotingPeriod

let vote voting conference =
  match conference.VotingPeriod with
  | Finished ->
      [VotingDenied "Voting Period Already Finished"|> DomainError]

  | InProgress ->
      match voting with
      | VoterIsNotAnOrganizer conference.Organizers _ ->
          [VotingDenied "Voter Is Not An Organizer" |> DomainError]

      | _ -> [VotingWasIssued voting]


let handleVote voting history =
  history
  |> conferenceState
  |> vote voting

let revokeVoting voting conference =
  match conference.VotingPeriod with
  | Finished ->
      [ RevocationOfVotingWasDenied (voting,"Voting Period Already Finished") |> DomainError ]

  | InProgress ->
      match voting with
      | VotingisNotIssued conference.Votings _ ->
          [ RevocationOfVotingWasDenied (voting,"Voting Not Issued") |> DomainError ]

      | _ -> [ VotingWasRevoked voting ]

let changeTitle title _ =
  [ TitleChanged title ]

let handleChangeTitle title history =
  history
  |> conferenceState
  |> changeTitle title

let decideNumberOfSlots number _ =
  [ NumberOfSlotsDecided number ]

let handleDecideNumberOfSlots number history =
  history
  |> conferenceState
  |> decideNumberOfSlots number

let handleRevokeVoting voting history =
  history
  |> conferenceState
  |> revokeVoting voting

let handleScheduleConference conference history =
  if history |> List.isEmpty then
    [ConferenceScheduled conference]
  else
    [ConferenceAlreadyScheduled |> DomainError]

let addOrganizerToConference organizer conference =
  match organizer with
  | OrganizerAlreadyInConference conference.Organizers _ ->
      [OrganizerAlreadyAddedToConference organizer |> DomainError]

  | _ -> [OrganizerAddedToConference organizer]

let private handleAddOrganizerToConference organizer history =
  history
  |> conferenceState
  |> addOrganizerToConference organizer

let removeOrganizerFromConference organizer conference =
  match organizer with
  | OrganizerNotInConference conference.Organizers _ ->
      [OrganizerWasNotAddedToConference organizer |> DomainError]

  | _ ->
    let revocations =
      conference.Votings
      |> votesOfOrganizer organizer.Id
      |> List.map VotingWasRevoked

    [OrganizerRemovedFromConference organizer] @ revocations

let private handleRemoveOrganizerFromConference organizer history =
  history
  |> conferenceState
  |> removeOrganizerFromConference organizer


let behaviour (command : Command) : EventProducer<Event> =
  match command with
  | ScheduleConference conference ->
       handleScheduleConference conference

  | ChangeTitle title ->
      handleChangeTitle title

  | DecideNumberOfSlots number ->
      handleDecideNumberOfSlots number

  | AddOrganizerToConference organizer ->
      handleAddOrganizerToConference organizer

  | RemoveOrganizerFromConference organizer ->
      handleRemoveOrganizerFromConference organizer

  | ProposeAbstract proposed ->
      handleProposeAbstract proposed

  | FinishVotingPeriod ->
      handleFinishVotingPeriod

  | ReopenVotingPeriod ->
      handleReopenVotingPeriod

  | Vote voting ->
      handleVote voting

  | RevokeVoting voting  ->
      handleRevokeVoting voting

