﻿using System;
using System.Collections.Generic;
using System.Linq;
using MTGAHelper.Entity;
using MTGAHelper.Lib.Cache;
using MTGAHelper.Lib.IO.Reader.MtgaOutputLog;
using MTGAHelper.Lib.IO.Reader.MtgaOutputLog.GRE.ClientToMatch;
using MTGAHelper.Lib.IO.Reader.MtgaOutputLog.GRE.MatchToClient;
using MTGAHelper.Lib.IO.Reader.MtgaOutputLog.GRE.MatchToClient.GameStateMessage.Raw;
using MTGAHelper.Lib.IO.Reader.MtgaOutputLog.UnityCrossThreadLogger;
using MTGAHelper.Lib.OutputLogParser.Models.GRE.ClientToMatch;
using MTGAHelper.Utility;
using Serilog;

namespace MTGAHelper.Lib.OutputLogParser.InMatchTracking
{
    public class InGameTracker2
    {
        /// <summary>Key: zoneId</summary>
        Dictionary<int, OwnedZone> zonesInfo;

        /// <summary>Key: instanceId</summary>
        readonly Dictionary<int, GameCardInZone> gameObjects = new Dictionary<int, GameCardInZone>();

        readonly IReadOnlyDictionary<int, Card> cardsByGrpId;

        public InGameTrackerState2 State { get; } = new InGameTrackerState2();

        public InGameTracker2(CacheSingleton<Dictionary<int, Card>> cardsSingleton)
        {
            cardsByGrpId = cardsSingleton.Get();
        }

        public override string ToString()
        {
            return $"State: {State}";
        }

        public void Reset(bool isBo3SoftReset = false)
        {
            zonesInfo = null;
            State.Reset(isBo3SoftReset);
        }

        public void ProcessMessage(IMtgaOutputLogPartResult message)
        {
            State.IsReset = false;

            switch (message)
            {
                case MatchCreatedResult matchCreated:
                    Reset();
                    State.OpponentScreenName = matchCreated.Raw.payload.opponentScreenName;
                    return;

                case MatchGameRoomStateChangedEventResult gameRoom:
                    {
                        var opponentInfo = gameRoom.Raw.gameRoomInfo.gameRoomConfig.reservedPlayers?.FirstOrDefault(i => i.playerName == State.OpponentScreenName);
                        if (opponentInfo != null)
                        {
                            State.SetSeatIds(State.OpponentSeatId == 1 ? 2 : 1, opponentInfo.systemSeatId);
                        }

                        return;
                    }

                case ConnectRespResult connectResp:
                    State.SetSeatIds(connectResp.Raw.systemSeatIds.First());

                    // only happens at start of game 1
                    State.SetLibraryGrpIds(connectResp.Raw.connectResp.deckMessage.deckCards);
                    State.SetSideboardGrpIds(connectResp.Raw.connectResp.deckMessage.sideboardCards);
                    return;

                case GameStateMessageResult gsm:
                    {
                        var gameStateMessage = gsm.Raw.gameStateMessage;
                        if (gameStateMessage.type == "GameStateType_Full" && zonesInfo == null)
                        {
                            // Initialization of the state (beginning of the match)
                            zonesInfo = gameStateMessage.zones.ToDictionary(z => z.zoneId, GetZoneAndOwnerFromGameStateZone);
                            // first gameStateMessage should not be processed as mulligan info will get messed up
                        }
                        else if (gameStateMessage.type == "GameStateType_Full" || gameStateMessage.type == "GameStateType_Diff")
                        {
                            var ti = gameStateMessage.turnInfo;
                            if (ti != null)
                                Log.Debug("=== game message t{t} {p}.{s} (p{p}) ===",
                                    ti.turnNumber, ti.phase, ti.step, ti.decisionPlayer);

                            AnalyzeDiff(gameStateMessage);
                        }
                        else
                        {
                            Log.Information("gameStateMessage with type {type} encountered", gameStateMessage.type);
                        }

                        return;
                    }

                case SelectNReqResult nReq when nReq.AllowCancel == AllowCancel.AllowCancel_No &&
                                                nReq.SeatId == State.MySeatId &&
                                                nReq.IdType == IdType.IdType_InstanceId:
                    {
                        Log.Information("option type: {OptionType}", nReq.OptionType);
                        State.SetInstIdsAboutToMove(nReq.Ids);
                        return;
                    }

                case GroupReqResult grpReq when grpReq.AllowCancel == AllowCancel.AllowCancel_No &&
                                                grpReq.SeatId == State.MySeatId:
                    State.SetInstIdsAboutToMove(grpReq.InstanceIds);
                    return;

                //case DuelSceneSideboardingStartResult _:
                case ClientToMatchResult<PayloadEnterSideboardingReq> submitDeckResp:
                    State.IsSideboarding = true;
                    return;

                case ClientToMatchResult<PayloadSubmitDeckResp> submitDeckResp:
                    {
                        State.IsSideboarding = false;
                        Reset(true);
                        // only happens after side boarding in BO3 matches, we need deck info from here in that case.
                        var deckSubmitted = submitDeckResp.Raw.payload.submitDeckResp.deck;
                        State.SetLibraryGrpIds(deckSubmitted.deckCards.Select(Convert.ToInt32));
                        State.SetSideboardGrpIds(deckSubmitted.sideboardCards.Select(Convert.ToInt32));
                        return;
                    }
            }
        }

        OwnedZone GetZoneAndOwnerFromGameStateZone(Zone zone)
        {
            Enum.TryParse(zone.type, out ZoneSimpleEnum zoneType);
            return zoneType.ToOwnedZone(State.MySeatId == zone.ownerSeatId);
        }


        void AnalyzeDiff(GameStateMessage gameStateMessage)
        {
            var priorityPlayer = gameStateMessage.turnInfo?.priorityPlayer;
            if (priorityPlayer.HasValue)
            {
                State.PriorityPlayer = priorityPlayer.Value;
            }

            if (gameStateMessage.players != null)
            {
                foreach (var p in gameStateMessage.players)
                {
                    // Update the players state
                    State.Players[p.systemSeatNumber] = p;
                }
            }

            var currentGameObjects = AddNewGameObjects(gameStateMessage.gameObjects);

            var annotationsByType = ParseAnnotations(gameStateMessage.annotations);
            Log.Debug("Annotations: {annotationsByType}", annotationsByType.Select(x => (x.Key, x.Count())));

            var objectIdChanges = ParseObjectIdChanges(annotationsByType);
            var zoneTransfers = ParseZoneTransfers(annotationsByType, objectIdChanges);

            var shuffles = ParseShuffles(annotationsByType);
            // Clear the Mind draws in the same message with the shuffle
            // so we have to handle shuffles before moves
            State.HandleShuffles(shuffles);

            State.HandleMoves(zoneTransfers);

            var instanceIdsInZones = gameStateMessage.zones?.ToDictionary(z => zonesInfo[z.zoneId], z => z.objectInstanceIds);

            var mulliganCount = gameStateMessage.players
                                    ?.FirstOrDefault(i => i.systemSeatNumber == State.MySeatId)
                                    ?.mulliganCount ?? 0;

            if (mulliganCount > State.MyMulliganCount)
            {
                // Mulligan
                State.MyMulliganCount = mulliganCount;
                State.DrawHands(instanceIdsInZones, currentGameObjects);
            }

            State.SetGameObjects(instanceIdsInZones, currentGameObjects);
        }

        static ILookup<AnnotationType, Annotation> ParseAnnotations(IEnumerable<Annotation> annotations)
        {
            return (annotations ?? Enumerable.Empty<Annotation>())
                .SelectMany(a => a.type.Select(t =>
                {
                    Enum.TryParse(t, out AnnotationType annType);
                    return (annType, a);
                }))
                .ToLookup(x => x.annType, x => x.a);
            //.ToDictionary(
            //    g => g.Key,
            //    g => (IReadOnlyCollection<Annotation>)g.Select(t => t.a).ToArray());
        }

        static IEnumerable<(IReadOnlyCollection<int> oldIds, IReadOnlyCollection<int> newIds)> ParseShuffles(ILookup<AnnotationType, Annotation> annotationsByType)
        {
            return annotationsByType[AnnotationType.AnnotationType_Shuffle]
                .Select(a => a.details)
                .Select(d => (
                    oldIds: (IReadOnlyCollection<int>)d.First(k => k.key == "OldIds").valueInt32,
                    newIds: (IReadOnlyCollection<int>)d.First(k => k.key == "NewIds").valueInt32));
        }

        static IReadOnlyCollection<(int origId, int newId)> ParseObjectIdChanges(ILookup<AnnotationType, Annotation> annotationsByType)
        {
            return annotationsByType[AnnotationType.AnnotationType_ObjectIdChanged]
                .Where(a => a.details != null)
                .Select(a => a.details)
                .Select(d => (
                    origId: d.First(x => x.key == "orig_id").valueInt32[0],
                    newId: d.First(x => x.key == "new_id").valueInt32[0]))
                .ToArray();
        }

        IReadOnlyCollection<ZoneTransferInfo2> ParseZoneTransfers(
            ILookup<AnnotationType, Annotation> annotationsByType,
            IReadOnlyCollection<(int oldId, int newId)> objectIdChanges)
        {
            var zoneTransfersRaw = annotationsByType[AnnotationType.AnnotationType_ZoneTransfer]
                .Select(ZoneTransferInfoFromAnnotation)
                .Where(z => z.IsValid);

            var zoneTransfers = zoneTransfersRaw
                    .Select(zt =>
                    {
                        try
                        {
                            var origId = objectIdChanges.FirstOrDefault(c => c.newId == zt.NewInstanceId).oldId;
                            zt.WithOldId(origId > 0 ? origId : zt.NewInstanceId);
                            // In most cases, the instanceId of the zoneTransfer is found within the gameObjects
                            // If the instanceId is not found, it has changed. Try to retrace it
                            if (gameObjects.TryGetValue(zt.NewInstanceId, out var card)
                                || gameObjects.TryGetValue(zt.OldInstanceId, out card))
                                return zt.WithGrpId(card.GrpId);

                            // no instance found. This happens for example if card move is from opponent's library to hand
                            return zt;
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "zonetransfer not as expected: {zt}", zt);
                            return zt;
                        }
                    })
                .ToArray();

            return zoneTransfers;
        }

        ZoneTransferInfo2 ZoneTransferInfoFromAnnotation(Annotation annotation)
        {
            try
            {
                var srcZoneId = annotation.details.First(x => x.key == "zone_src").valueInt32.Single();
                var srcZone = zonesInfo[srcZoneId];
                var destZoneId = annotation.details.First(x => x.key == "zone_dest").valueInt32.Single();
                var destZone = zonesInfo[destZoneId];
                var instanceId = annotation.affectedIds.Single();
                var category = annotation.details.FirstOrDefault(x => x.key == "category")?.valueString
                    .SingleOrDefault();
                return new ZoneTransferInfo2(instanceId, srcZone, destZone, category);
            }
            catch
            {
                return ZoneTransferInfo2.Invalid;
            }
        }

        IReadOnlyCollection<GameCardInZone> AddNewGameObjects(IEnumerable<GameObject> gameStateObjects)
        {
            if (gameStateObjects == null)
                return null;

            var currentGameObjects = gameStateObjects
                .Where(o => o.zoneId.HasValue)
                .Select(o =>
                {
                    Enum.TryParse(o.type, out GameObjectType goType);
                    Enum.TryParse(o.visibility, out Visibility visibility);
                    var card = cardsByGrpId.GetValueOrDefault(o.grpId);
                    if (card?.linkedFaceType == enumLinkedFace.AdventureAdventure)
                    {
                        // display the creature half of adventure cards
                        card = cardsByGrpId.GetValueOrDefault(card.LinkedCardGrpId);
                    }

                    return new GameCardInZone(
                        o.instanceId,
                        o.ownerSeatId,
                        zonesInfo[o.zoneId.Value],
                        card?.grpId ?? o.grpId,
                        goType,
                        visibility,
                        card);
                })
                .ToArray();

            foreach (var cardInZone in currentGameObjects)
            {
                if (gameObjects.ContainsKey(cardInZone.InstId))
                    gameObjects[cardInZone.InstId] = cardInZone;
                else
                    gameObjects.Add(cardInZone.InstId, cardInZone);
            }

            return currentGameObjects;
        }
    }
}
