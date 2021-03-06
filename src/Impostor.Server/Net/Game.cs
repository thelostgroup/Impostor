﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Hazel;
using Impostor.Server.Data;
using Impostor.Server.Exceptions;
using Impostor.Server.Extensions;
using Impostor.Server.Net.Response;
using Impostor.Shared.Innersloth;
using Impostor.Shared.Innersloth.Data;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Impostor.Server.Net
{
    public class Game
    {
        private static readonly ILogger Logger = Log.ForContext<Game>();
        
        private readonly GameManager _gameManager;
        private readonly ConcurrentDictionary<int, ClientPlayer> _players;
        private readonly HashSet<IPAddress> _bannedIps;

        public Game(GameManager gameManager, int code, GameOptionsData options)
        {
            _gameManager = gameManager;
            _players = new ConcurrentDictionary<int, ClientPlayer>();
            _bannedIps = new HashSet<IPAddress>();
            
            Code = code;
            CodeStr = GameCode.IntToGameName(code);
            HostId = -1;
            GameState = GameStates.NotStarted;
            Options = options;
        }
        
        public int Code { get; }
        public string CodeStr { get; }
        public bool IsPublic { get; private set; }
        public int HostId { get; private set; }
        public GameStates GameState { get; private set; }
        public GameOptionsData Options { get; }

        public void SendToAllExcept(MessageWriter message, ClientPlayer sender)
        {
            foreach (var (_, player) in _players.Where(x => x.Value != sender))
            {
                if (player.Client.Connection.State != ConnectionState.Connected)
                {
                    Logger.Warning("[{0}] Tried to sent data to a disconnected player ({1}).", sender?.Client.Id, player.Client.Id);
                    continue;
                }
                
                player.Client.Send(message);
            }
        }

        public void SendTo(MessageWriter message, int playerId)
        {
            if (_players.TryGetValue(playerId, out var player))
            {
                if (player.Client.Connection.State != ConnectionState.Connected)
                {
                    Logger.Warning("[{0}] Sending data to {1} failed, player is not connected.", CodeStr, player.Client.Id);
                    return;
                }
                
                player.Client.Send(message);
            }
            else
            {
                Logger.Warning("[{0}] Sending data to {1} failed, player does not exist.", CodeStr, playerId);
            }
        }

        public void HandleStartGame(MessageReader message)
        {
            GameState = GameStates.Started;
            
            using (var packet = MessageWriter.Get(SendOption.Reliable))
            {
                packet.CopyFrom(message);
                SendToAllExcept(packet, null);
            }
        }

        public void HandleJoinGame(ClientPlayer sender)
        {
            if (_bannedIps.Contains(sender.Client.Connection.EndPoint.Address))
            {
                sender.Client.Connection.Send(new Message1DisconnectReason(DisconnectReason.Banned));
                return;
            }
            
            switch (GameState)
            {
                case GameStates.NotStarted:
                    HandleJoinGameNew(sender);
                    break;
                case GameStates.Ended:
                    HandleJoinGameNext(sender);
                    break;
                case GameStates.Started:
                    sender.Client.Connection.Send(new Message1DisconnectReason(DisconnectReason.GameStarted));
                    return;
                case GameStates.Destroyed:
                    sender.Client.Connection.Send(new Message1DisconnectReason(DisconnectReason.Custom, DisconnectMessages.Destroyed));
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void HandleEndGame(MessageReader message)
        {
            GameState = GameStates.Ended;
            
            // Broadcast end of the game.
            using (var packet = MessageWriter.Get(SendOption.Reliable))
            {
                packet.CopyFrom(message);
                SendToAllExcept(packet, null);
            }
            
            // Remove all players from this game.
            foreach (var player in _players)
            {
                player.Value.Game = null;
            }
            
            _players.Clear();
        }

        public void HandleAlterGame(MessageReader message, ClientPlayer sender, bool isPublic)
        {
            IsPublic = isPublic;
            
            using (var packet = MessageWriter.Get(SendOption.Reliable))
            {
                packet.CopyFrom(message);
                SendToAllExcept(packet, sender);
            }
        }
        
        public void HandleRemovePlayer(int playerId, DisconnectReason reason)
        {
            if (_players.TryRemove(playerId, out var player))
            {
                player.Game = null;
            }
            
            Logger.Information("{0} - Player {1} ({2}) has left.", CodeStr, player?.Client.Name, playerId);

            // Game is empty, remove it.
            if (_players.Count == 0)
            {
                GameState = GameStates.Destroyed;

                // Remove instance reference.
                _gameManager.Remove(Code);
                return;
            }

            // Host migration.
            if (HostId == playerId)
            {
                var newHost = _players.First().Value;
                HostId = newHost.Client.Id;
                Logger.Information("{0} - Assigned {1} ({2}) as new host.", CodeStr, newHost.Client.Name, newHost.Client.Id);
            }

            using (var packet = MessageWriter.Get(SendOption.Reliable))
            {
                WriteRemovePlayerMessage(packet, false, playerId, reason);
                SendToAllExcept(packet, player);
            }
        }

        public void HandleKickPlayer(int playerId, bool isBan)
        {
            _players.TryGetValue(playerId, out var p);
            Logger.Information("{0} - Player {1} ({2}) has left.", CodeStr, p?.Client.Name, playerId);
            
            using (var message = MessageWriter.Get(SendOption.Reliable))
            {
                WriteKickPlayerMessage(message, false, playerId, isBan);
                SendToAllExcept(message, null);
                
                if (_players.TryRemove(playerId, out var player))
                {
                    player.Game = null;

                    if (isBan)
                    {
                        _bannedIps.Add(player.Client.Connection.EndPoint.Address);
                    }
                }
                
                WriteRemovePlayerMessage(message, true, playerId, isBan 
                    ? DisconnectReason.Banned 
                    : DisconnectReason.Kicked);
                SendToAllExcept(message, player);
            }
        }
        
        private void HandleJoinGameNew(ClientPlayer sender)
        {
            Logger.Information("{0} - Player {1} ({2}) is joining.", CodeStr, sender.Client.Name, sender.Client.Id);
            
            // Store player.
            if (!_players.TryAdd(sender.Client.Id, sender))
            {
                throw new AmongUsException("Failed to add player to game.");
            }
            
            // Assign player to this game for future packets.
            sender.Game = this;

            // Assign hostId if none is set.
            if (HostId == -1)
            {
                HostId = sender.Client.Id;
            }

            if (HostId == sender.Client.Id)
            {
                sender.LimboState = LimboStates.NotLimbo;
            }

            using (var message = MessageWriter.Get(SendOption.Reliable))
            {
                WriteJoinedGameMessage(message, false, sender);
                WriteAlterGameMessage(message, false);
                
                sender.Client.Send(message);

                BroadcastJoinMessage(message, true, sender);
            }
        }

        private void HandleJoinGameNext(ClientPlayer sender)
        {
            Logger.Information("{0} - Player {1} ({2}) is rejoining.", CodeStr, sender.Client.Name, sender.Client.Id);
            
            if (sender.Client.Id == HostId)
            {
                GameState = GameStates.NotStarted;
                HandleJoinGameNew(sender);

                using (var message = MessageWriter.Get(SendOption.Reliable))
                {
                    foreach (var (_, player) in _players.Where(x => x.Value != sender))
                    {
                        WriteJoinedGameMessage(message, true, player);
                        WriteAlterGameMessage(message, false);
                        player.Client.Send(message);
                    }
                }
                
                return;
            }

            if (_players.Count >= 9)
            {
                sender.Client.Connection.Send(new Message1DisconnectReason(DisconnectReason.GameFull));
                return;
            }

            // Store player.
            if (!_players.TryAdd(sender.Client.Id, sender))
            {
                throw new AmongUsException("Failed to add player to game.");
            }
            
            // Assign player to this game for future packets.
            sender.Game = this;
            
            // Limbo, yes.
            sender.LimboState = LimboStates.WaitingForHost;

            using (var packet = MessageWriter.Get(SendOption.Reliable))
            {
                WriteWaitForHostMessage(packet, false, sender);
                sender.Client.Send(packet);

                BroadcastJoinMessage(packet, true, sender);
            }
        }

        private void WriteRemovePlayerMessage(MessageWriter message, bool clear, int playerId, DisconnectReason reason)
        {
            // Only a subset of DisconnectReason shows an unique message.
            // ExitGame, Banned and Kicked.
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.RemovePlayer);
            message.Write(Code);
            message.Write(playerId);
            message.Write(HostId);
            message.Write((byte) reason);
            message.EndMessage();
        }
        
        private void WriteJoinedGameMessage(MessageWriter message, bool clear, ClientPlayer player)
        {
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.JoinedGame);
            message.Write(Code);
            message.Write(player.Client.Id);
            message.Write(HostId);
            message.WritePacked(_players.Count - 1);
            
            foreach (var (_, p) in _players.Where(x => x.Value != player))
            {
                message.WritePacked(p.Client.Id);
            }
            
            message.EndMessage();
        }

        private void WriteAlterGameMessage(MessageWriter message, bool clear)
        {
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.AlterGame);
            message.Write(Code);
            message.Write((byte) AlterGameTags.ChangePrivacy);
            message.Write(IsPublic);
            message.EndMessage();
        }

        private void WriteKickPlayerMessage(MessageWriter message, bool clear, int playerId, bool isBan)
        {
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.KickPlayer);
            message.Write(Code);
            message.WritePacked(playerId);
            message.Write(isBan);
            message.EndMessage();
        }
        
        private void WriteWaitForHostMessage(MessageWriter message, bool clear, ClientPlayer player)
        {
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.WaitForHost);
            message.Write(Code);
            message.Write(player.Client.Id);
            message.EndMessage();
        }
        
        private void BroadcastJoinMessage(MessageWriter message, bool clear, ClientPlayer player)
        {
            if (clear)
            {
                message.Clear(SendOption.Reliable);
            }
            
            message.StartMessage((byte) RequestFlag.JoinGame);
            message.Write(Code);
            message.Write(player.Client.Id);
            message.Write(HostId);
            message.EndMessage();
            
            SendToAllExcept(message, player);
        }
    }
}