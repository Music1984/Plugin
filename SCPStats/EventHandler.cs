﻿// -----------------------------------------------------------------------
// <copyright file="EventHandler.cs" company="SCPStats.com">
// Copyright (c) SCPStats.com. All rights reserved.
// Licensed under the Apache v2 license.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using InventorySystem.Items;
using InventorySystem.Items.Pickups;
using InventorySystem.Items.ThrowableProjectiles;
using LiteNetLib;
using MEC;
using PlayerRoles;
using PlayerStatsSystem;
using PluginAPI.Core;
using PluginAPI.Core.Attributes;
using PluginAPI.Enums;
using PluginAPI.Events;
using PluginAPI.Helpers;
using SCPStats.Commands;
using SCPStats.Hats;
using SCPStats.Websocket;
using SCPStats.Websocket.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SCPStats
{
#pragma warning disable 4014
    public class EventHandler
    {
        private static bool DidRoundEnd = false;
        private static bool Restarting = false;
        private static List<string> Players = new List<string>();

        private static bool firstJoin = true;
        private static bool firstRound = true;

        private static Dictionary<string, string> PocketPlayers = new Dictionary<string, string>();
        private static List<string> JustJoined = new List<string>();

        internal static bool RanServer = false;

        public static bool PauseRound = SCPStats.Singleton?.Config?.DisableRecordingStats ?? false;

        private static List<CoroutineHandle> coroutines = new List<CoroutineHandle>();
        private static List<string> SpawnsDone = new List<string>();

        //Tuple<PreauthFlags, UserInfo>.
        internal static Dictionary<string, Tuple<CentralAuthPreauthFlags?, UserInfoData>> UserInfo = new Dictionary<string, Tuple<CentralAuthPreauthFlags?, UserInfoData>>();
        private static List<string> PreRequestedIDs = new List<string>();
        internal static List<string> DelayedIDs = new List<string>();

        internal static Dictionary<string, Int64> LocalBanCache = new Dictionary<string, Int64>();

        internal static void Reset()
        {
            Timing.KillCoroutines(coroutines.ToArray());
            coroutines.Clear();

            WebsocketHandler.Stop();
            MessageIDsStore.Reset();

            SpawnsDone.Clear();
            PocketPlayers.Clear();
            JustJoined.Clear();

            UserInfo.Clear();
            PreRequestedIDs.Clear();
            DelayedIDs.Clear();

            PauseRound = SCPStats.Singleton?.Config?.DisableRecordingStats ?? false;
        }

        internal static void ClearUserInfo()
        {
            var ids = Player.GetPlayers().Select(Helper.HandleId);
            UserInfo = UserInfo.Where((kvp) => ids.Contains(kvp.Key)).ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value);
        }

        internal static void Start()
        {
            firstJoin = true;

            WebsocketHandler.Start();

            OnRAReload();
        }

        private static IEnumerator<float> ClearPlayers()
        {
            yield return Timing.WaitForSeconds(30f);

            for (var i = 0; i < Players.Count; i++)
            {
                var player = Players[i];
                if (Player.GetPlayers().Any(p => p != null && !p.IsServer && p.UserId == player)) continue;
                
                WebsocketHandler.SendRequest(RequestType.Leave, "{\"playerid\":\"" + Helper.HandleId(player) + "\"}");

                Players.Remove(player);
            }
        }

        internal static void OnRAReload()
        {
            //Timing.RunCoroutine(RAReloaded());
            // TODO: Implement.
        }

        private static IEnumerator<float> RAReloaded()
        {
            yield return Timing.WaitForSeconds(1.5f);

            ClearUserInfo();

            var ids = (from player in Player.GetPlayers() where player?.UserId != null && !player.IsServer && player.IsReady && !Helper.IsPlayerNPC(player) select new Tuple<string, string>(Helper.HandleId(player), player.IpAddress.Trim().ToLower())).ToList();

            foreach (var (id, ip) in ids)
            {
                WebsocketHandler.SendRequest(RequestType.UserInfo, Helper.UserInfoData(id, ip));

                yield return Timing.WaitForSeconds(.1f);
            }
        }

        private static bool IsGamemodeRunning()
        {
            /*var gamemodeManager = Loader.Plugins.FirstOrDefault(pl => pl.Name == "Gamemode Manager");
            if (gamemodeManager == null) return false;
            
            var pluginType = gamemodeManager.Assembly.GetType("Plugin");
            if (pluginType == null) return false;
            
            var queueHandler = gamemodeManager.Assembly.GetType("QueueHandler");
            if (queueHandler == null) return false;

            var queueHandlerInstance = pluginType.GetField("QueueHandler")?.GetValue(gamemodeManager);
            if (queueHandlerInstance == null) return false;

            return (bool) (queueHandler.GetProperty("IsAnyGamemodeActive")?.GetValue(queueHandlerInstance) ?? false);*/
            return false;
        }

        [PluginEvent(ServerEventType.RoundStart)]
        internal void OnRoundStart()
        {
            Restarting = false;
            DidRoundEnd = false;

            if (IsGamemodeRunning())
            {
                PauseRound = true;
            }

            WebsocketHandler.SendRequest(RequestType.RoundStart);

            Timing.RunCoroutine(SendStart());

            PreRequestedIDs.Clear();
            DelayedIDs.Clear();

            firstRound = false;

        }

        private static IEnumerator<float> SendStart()
        {
            yield return Timing.WaitForSeconds(.2f);

            if (!Helper.IsRoundRunning()) yield break;

            var ids = new List<PlayerInfo>();

            foreach (var player in Player.GetPlayers())
            {
                var playerInfo = Helper.GetPlayerInfo(player, false, false);
                if (player?.UserId == null || !playerInfo.IsAllowed || playerInfo.PlayerID == null || player.DoNotTrack || player.Role == RoleTypeId.None || player.Role == RoleTypeId.Spectator) continue;
                
                ids.Add(playerInfo);
            }

            foreach (var playerInfo in ids)
            {
                WebsocketHandler.SendRequest(RequestType.Spawn, "{\"playerid\":\"" + playerInfo.PlayerID + "\",\"spawnrole\":\"" + playerInfo.PlayerRole.ToID() + "\"}");
                yield return Timing.WaitForSeconds(.05f);
            }
        }

        [PluginEvent(ServerEventType.RoundEnd)]
        internal void OnRoundEnding()
        {
            // TODO: Track leading team.
            SendRoundEnd("-1");
        }
        
        [PluginEvent(ServerEventType.RoundRestart)]
        internal void OnRoundRestart()
        {
            SendRoundEnd("-1");
        }

        private static void SendRoundEnd(string leadingTeam)
        {
            if (DidRoundEnd) return;
            
            foreach (var player in Player.GetPlayers())
            {
                if (player?.UserId == null || player.IsServer || Helper.IsPlayerNPC(player) || !player.IsReady || Players.Contains(player.UserId)) continue;
                
                Players.Add(player.UserId);
            }
            
            Restarting = true;
            HatCommand.HatPlayers.Clear();
            DidRoundEnd = true;

            WebsocketHandler.SendRequest(RequestType.RoundEnd, leadingTeam);
            Timing.RunCoroutine(SendWinsLose(leadingTeam));

            Timing.KillCoroutines(coroutines.ToArray());
            coroutines.Clear();

            SpawnsDone.Clear();
            PocketPlayers.Clear();
            JustJoined.Clear();

            ClearUserInfo();
            Timing.RunCoroutine(GetRoundEndUsers());
        }

        private static IEnumerator<float> GetRoundEndUsers()
        {
            var ids = (from player in Player.GetPlayers() where player?.UserId != null && !player.IsServer && player.IsReady && !Helper.IsPlayerNPC(player) select new Tuple<string, string>(Helper.HandleId(player), player.IpAddress.Trim().ToLower())).ToList();
            PreRequestedIDs = ids.Select(tuple => tuple.Item1).ToList();

            foreach (var (id, ip) in ids)
            {
                WebsocketHandler.SendRequest(RequestType.UserInfo, Helper.UserInfoData(id, ip));

                yield return Timing.WaitForSeconds(.1f);
            }
        }

        private static IEnumerator<float> SendWinsLose(string leadingTeam)
        {
            var winLose = new Dictionary<string, Tuple<bool, bool, RoleTypeId>>();

            foreach (var player in Player.GetPlayers())
            {
                var playerInfo = Helper.GetPlayerInfo(player, false, false);
                if (!playerInfo.IsAllowed || playerInfo.PlayerID == null) continue;

                if (PauseRound || Helper.IsPlayerTutorial(player) || player.IsOverwatchEnabled)
                {
                    winLose[playerInfo.PlayerID] = new Tuple<bool, bool, RoleTypeId>(false, true, playerInfo.PlayerRole);
                }
                else if (playerInfo.PlayerRole != RoleTypeId.None && playerInfo.PlayerRole != RoleTypeId.Spectator && !Helper.IsPlayerGhost(player))
                {
                    winLose[playerInfo.PlayerID] = new Tuple<bool, bool, RoleTypeId>(true, false, playerInfo.PlayerRole);
                }
                else
                {
                    winLose[playerInfo.PlayerID] = new Tuple<bool, bool, RoleTypeId>(false, false, playerInfo.PlayerRole);
                }
            }

            foreach (var keys in winLose)
            {
                if (keys.Value.Item2)
                {
                    WebsocketHandler.SendRequest(RequestType.RoundEndPlayer, keys.Key);
                }
                else if (keys.Value.Item1)
                {
                    WebsocketHandler.SendRequest(RequestType.Win, "{\"playerid\":\""+keys.Key+"\",\"role\":\""+keys.Value.Item3.ToID()+"\",\"team\":\""+leadingTeam+"\"}");
                }
                else
                {
                    WebsocketHandler.SendRequest(RequestType.Lose, "{\"playerid\":\""+keys.Key+"\",\"team\":\""+leadingTeam+"\"}");
                }
                
                yield return Timing.WaitForSeconds(.05f);
            }
        }

        [PluginEvent(ServerEventType.WaitingForPlayers)]
        internal void Waiting()
        {
            coroutines.Add(Timing.RunCoroutine(ClearPlayers()));
            
            Restarting = false;
            DidRoundEnd = false;
            PauseRound = SCPStats.Singleton?.Config?.DisableRecordingStats ?? false;
        }
        
        [PluginEvent(ServerEventType.PlayerDeath)]
        internal void OnKill(Player target, Player attacker, DamageHandlerBase damageHandler)
        {
            if (!Helper.IsRoundRunning()) return;

            var killerInfo = Helper.GetFootprintInfo(damageHandler is AttackerDamageHandler attack ? attack.Attacker : default);
            var targetInfo = Helper.GetPlayerInfo(target);

            if (!killerInfo.IsAllowed || !targetInfo.IsAllowed || (killerInfo.PlayerID == null && targetInfo.PlayerID == null) || targetInfo.PlayerRole == RoleTypeId.None || targetInfo.PlayerRole == RoleTypeId.Spectator) return;

            var damageID = damageHandler.ToID();
            
            if (damageID == 10 /* Pocket ID */ && PocketPlayers.TryGetValue(targetInfo.PlayerID, out var killer))
            {
                killerInfo.PlayerID = killer;
                killerInfo.PlayerRole = RoleTypeId.Scp106;
            }
            else if (killerInfo.PlayerID == null && killerInfo.PlayerRole == RoleTypeId.None)
            {
                killerInfo.PlayerID = targetInfo.PlayerID;
                killerInfo.PlayerRole = targetInfo.PlayerRole;
            }

            WebsocketHandler.SendRequest(RequestType.KillDeath, "{\"killerID\":\""+killerInfo.PlayerID+"\",\"killerRole\":\""+killerInfo.PlayerRole.ToID()+"\",\"targetID\":\""+targetInfo.PlayerID+"\",\"targetRole\":\""+targetInfo.PlayerRole.ToID()+"\",\"damageType\":\""+damageID+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerChangeRole)]
        internal void OnRoleChanged(Player player, PlayerRoleBase oldRole, RoleTypeId newRole, RoleChangeReason changeReason)
        {
            if (player?.UserId != null && player.GameObject != null && !player.IsServer)
            {
                if (newRole != RoleTypeId.None && newRole != RoleTypeId.Spectator)
                {
                    Timing.CallDelayed(.5f, player.SpawnCurrentHat);
                } 
                else if (player.GameObject.TryGetComponent<HatPlayerComponent>(out var hatPlayerComponent) && hatPlayerComponent.item != null && hatPlayerComponent.item.gameObject != null)
                {
                    Timing.CallDelayed(.5f, () => UnityEngine.Object.Destroy(hatPlayerComponent.item.gameObject));
                }
            }

            var playerInfo = Helper.GetPlayerInfo(player, false, false);
            if (!playerInfo.IsAllowed) return;

            if (Statistics.Round.Duration.TotalSeconds < 5 || !Helper.IsRoundRunning()) return;

            if (changeReason == RoleChangeReason.Escaped)
            {
                var cuffer = (player?.IsDisarmed ?? false) && player.DisarmedBy?.UserId != null ? Helper.GetPlayerInfo(player.DisarmedBy) : new PlayerInfo(null, RoleTypeId.None, true);

                if (!cuffer.IsAllowed || cuffer.PlayerID == playerInfo.PlayerID)
                {
                    cuffer.PlayerID = null;
                    cuffer.PlayerRole = RoleTypeId.None;
                }
                if(playerInfo.PlayerID != null || cuffer.PlayerID != null) WebsocketHandler.SendRequest(RequestType.Escape, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"role\":\""+playerInfo.PlayerRole.ToID()+"\",\"cufferid\":\""+cuffer.PlayerID+"\",\"cufferrole\":\""+cuffer.PlayerRole.ToID()+"\"}");
            }

            if (playerInfo.PlayerID == null) return;

            WebsocketHandler.SendRequest(RequestType.Spawn, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"spawnrole\":\""+newRole.ToID()+"\",\"reason\":\""+changeReason.ToID()+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerSearchedPickup)]
        internal bool OnPickup(Player player, ItemPickupBase item)
        {
            if (!item || !item.gameObject) return true;
            
            if (item.gameObject.TryGetComponent<HatItemComponent>(out var hat))
            {
                if (player?.UserId != null && !player.IsServer && player.IsReady && player.IpAddress != "127.0.0.WAN" && player.IpAddress != "127.0.0.1" && (hat.player == null || hat.player.gameObject != player?.GameObject) && (SCPStats.Singleton?.Config.DisplayHatHint ?? true))
                {
                    player.ReceiveHint(SCPStats.Singleton?.Translation?.HatHint ?? "You can get a hat like this at patreon.com/SCPStats.", 2f);
                }

                return false;
            }

            var playerInfo = Helper.GetPlayerInfo(player);
            if (!playerInfo.IsAllowed || playerInfo.PlayerID == null || !Helper.IsRoundRunning()) return true;
            
            WebsocketHandler.SendRequest(RequestType.Pickup, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"itemid\":\""+item.Info.ItemId.ToID()+"\"}");

            return true;
        }

        [PluginEvent(ServerEventType.PlayerDropItem)]
        internal void OnDrop(Player player, ItemBase item)
        {
            if (item == null || !Helper.IsRoundRunning()) return;
            
            var playerInfo = Helper.GetPlayerInfo(player);
            if (!playerInfo.IsAllowed || playerInfo.PlayerID == null) return;
            
            WebsocketHandler.SendRequest(RequestType.Drop, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"itemid\":\""+item.ItemTypeId.ToID()+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerPickupAmmo)]
        internal bool OnPickupAmmo(Player player, ItemPickupBase item)
        {
            if (!item || !item.gameObject || !item.gameObject.TryGetComponent<HatItemComponent>(out var hat)) return true;

            if (player?.UserId != null && !player.IsServer && player.IsReady && player.IpAddress != "127.0.0.WAN" && player.IpAddress != "127.0.0.1" && (hat.player == null || hat.player.gameObject != player?.GameObject) && (SCPStats.Singleton?.Config.DisplayHatHint ?? true))
            {
                player.ReceiveHint(SCPStats.Singleton?.Translation?.HatHint ?? "You can get a hat like this at patreon.com/SCPStats.", 2f);
            }

            return false;
        }

        [PluginEvent(ServerEventType.PlayerJoined)]
        internal void OnJoin(Player player)
        {
            if (player?.UserId == null || player.IsServer || !player.IsReady || Helper.IsPlayerNPC(player)) return;

            if (firstJoin)
            {
                firstJoin = false;
                Verification.UpdateID();
            }

            if (WebsocketRequests.RunUserInfo(player)) return;

            var id = Helper.HandleId(player);

            JustJoined.Add(player.UserId);
            Timing.CallDelayed(10f, () =>
            {
                JustJoined.Remove(player.UserId);
            });

            var isInvalid = !Round.IsRoundStarted && Players.Contains(player.UserId);

            WebsocketHandler.SendRequest(RequestType.Join, "{\"playerid\":\""+id+"\""+((SCPStats.Singleton?.Config?.SendPlayerNames ?? false) ? ",\"playername\":\""+player.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\"" : "")+(isInvalid ? ",\"invalid\":true" : "")+(player.DoNotTrack ? ",\"dnt\":true" : "")+"}");

            if (isInvalid) return;

            Players.Add(player.UserId);
        }

        [PluginEvent(ServerEventType.PlayerLeft)]
        internal void OnLeave(Player player)
        {
            if (player?.UserId == null || player.IsServer || !player.IsReady || Helper.IsPlayerNPC(player)) return;

            if (player.GameObject != null && player.GameObject.TryGetComponent<HatPlayerComponent>(out var playerComponent) && playerComponent.item != null)
            {
                Object.Destroy(playerComponent.item.gameObject);
                playerComponent.item = null;
            }

            if (Restarting) return;

            var id = Helper.HandleId(player);

            if (UserInfo.ContainsKey(id)) UserInfo.Remove(id);
            if (Players.Contains(player.UserId)) Players.Remove(player.UserId);

            WebsocketHandler.SendRequest(RequestType.Leave, "{\"playerid\":\""+id+"\""+(player.DoNotTrack ? ",\"dnt\":true" : "")+"}");
        }

        [PluginEvent(ServerEventType.PlayerUsedItem)]
        internal void OnUse(Player player, ItemBase item)
        {
            if (item == null) return;

            var playerInfo = Helper.GetPlayerInfo(player);
            if (!playerInfo.IsAllowed || playerInfo.PlayerID == null || !Helper.IsRoundRunning()) return;
            
            WebsocketHandler.SendRequest(RequestType.Use, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"itemid\":\""+item.ItemTypeId.ToID()+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerThrowProjectile)]
        internal void OnThrow(Player player, ThrowableItem item, float forceAmount, float upwardsFactor, Vector3 torque, Vector3 velocity)
        {
            if (item == null || !Helper.IsRoundRunning()) return;
            
            var playerInfo = Helper.GetPlayerInfo(player);
            if (!playerInfo.IsAllowed || playerInfo.PlayerID == null) return;
            
            WebsocketHandler.SendRequest(RequestType.Use, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"itemid\":\""+item.ItemTypeId.ToID()+"\"}");
        }

        [PluginEvent(ServerEventType.Scp914UpgradePickup)]
        internal bool OnUpgrade(ItemPickupBase item)
        {
            if (item == null || item.gameObject == null) return true;
            if (item.gameObject.TryGetComponent<HatItemComponent>(out _)) return false;

            return true;
        }

        [PluginEvent(ServerEventType.Scp106TeleportPlayer)]
        internal void OnEnterPocketDimension(Player player, Player scp106)
        {
            if (!Helper.IsRoundRunning()) return;
            
            var playerInfo = Helper.GetPlayerInfo(player);
            var scp106Info = Helper.GetPlayerInfo(scp106);

            if (playerInfo.PlayerID == scp106Info.PlayerID) scp106Info.PlayerID = null;
            if (playerInfo.PlayerID == null && scp106Info.PlayerID == null) return;

            WebsocketHandler.SendRequest(RequestType.PocketEnter, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"playerrole\":\""+playerInfo.PlayerRole.ToID()+"\",\"scp106\":\""+scp106Info.PlayerID+"\"}");

            if (playerInfo.PlayerID == null || scp106Info.PlayerID == null) return;
            PocketPlayers[playerInfo.PlayerID] = scp106Info.PlayerID;
        }

        [PluginEvent(ServerEventType.PlayerExitPocketDimension)]
        internal void OnEscapingPocketDimension(Player player, bool isSuccessful)
        {
            if (!isSuccessful || !Helper.IsRoundRunning()) return;
            
            var playerInfo = Helper.GetPlayerInfo(player);
            if (!playerInfo.IsAllowed || playerInfo.PlayerID == null) return;
            
            PocketPlayers.TryGetValue(playerInfo.PlayerID, out var scp106ID);
            
            WebsocketHandler.SendRequest(RequestType.PocketExit, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"playerrole\":\""+playerInfo.PlayerRole.ToID()+"\",\"scp106\":\""+scp106ID+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerBanned)]
        internal void OnBan(Player target, Player issuer, string reason, long duration)
        {
            // TODO: Offline bans.
            if (!(SCPStats.Singleton?.Config?.ModerationLogging ?? true) || target == null) return;

            //var name = target?.UserId != null ? target.Nickname : ev.Details.OriginalName;
            var name = target.Nickname;
            var ip = (SCPStats.Singleton?.Config?.LinkIpsToBans ?? false) ? Helper.HandleIP(target) : null;

            //WebsocketHandler.SendRequest(RequestType.AddWarning, "{\"type\":\"1\",\"playerId\":\""+Helper.HandleId(ev.Details.Id) + (ip != null ? "\",\"playerIP\":\"" + ip : "") + "\",\"message\":\""+ev.Details.Reason.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"length\":"+((long) TimeSpan.FromTicks(ev.Details.Expires-ev.Details.IssuanceTime).TotalSeconds)+",\"playerName\":\""+name.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"issuer\":\""+(!string.IsNullOrEmpty(issuer?.UserId) && !(issuer?.IsServer ?? false) ? Helper.HandleId(issuer) : "")+"\",\"issuerName\":\""+(!string.IsNullOrEmpty(issuer?.Nickname) && !(issuer?.IsServer ?? false) ? issuer.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"") : "")+"\"}");
            WebsocketHandler.SendRequest(RequestType.AddWarning, "{\"type\":\"1\",\"playerId\":\""+Helper.HandleId(target) + (ip != null ? "\",\"playerIP\":\"" + ip : "") + "\",\"message\":\""+reason.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"length\":"+duration+",\"playerName\":\""+name.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"issuer\":\""+(!string.IsNullOrEmpty(issuer?.UserId) && !(issuer?.IsServer ?? false) ? Helper.HandleId(issuer) : "")+"\",\"issuerName\":\""+(!string.IsNullOrEmpty(issuer?.Nickname) && !(issuer?.IsServer ?? false) ? issuer.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"") : "")+"\"}");
            
            Timing.RunCoroutine(UpdateLocalBanCache());
        }
        
        private static List<string> IgnoredMessages = new List<string>()
        {
            "[SCPStats]",
            "VPNs and proxies are forbidden",
            "<size=70><color=red>You are banned.",
            "Your account must be at least",
            "You have been banned.",
            "[Kicked by uAFK]",
            "You were AFK",
            "[Anty-AFK]",
            "[Anty AFK]",
            "Auto-Kick:",
            "[Auto-Kick]",
            "[Auto Kick]"
        };
        
        internal static List<string> IgnoredMessagesFromIntegration = new List<string>();
        
        [PluginEvent(ServerEventType.PlayerKicked)]
        internal void OnKick(Player target, Player issuer, string reason)
        {
            if (!(SCPStats.Singleton?.Config?.ModerationLogging ?? true) || target?.UserId == null || target.IsServer || !target.IsReady || Helper.IsPlayerNPC(target) || JustJoined.Contains(target.UserId) || (SCPStats.Singleton?.Translation?.BannedMessage != null && reason.StartsWith(SCPStats.Singleton.Translation.BannedMessage.Split('{').First())) || (SCPStats.Singleton?.Translation?.WhitelistKickMessage != null && reason.StartsWith(SCPStats.Singleton.Translation.WhitelistKickMessage)) || (SCPStats.Singleton?.Config?.IgnoredMessages ?? IgnoredMessages).Any(val => reason.StartsWith(val)) || IgnoredMessagesFromIntegration.Any(val => reason.StartsWith(val))) return;

            WebsocketHandler.SendRequest(RequestType.AddWarning, "{\"type\":\"2\",\"playerId\":\""+Helper.HandleId(target.UserId)+"\",\"message\":\""+reason.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"playerName\":\""+target.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"issuer\":\""+(!string.IsNullOrEmpty(issuer?.UserId) && !(issuer?.IsServer ?? false) ? Helper.HandleId(issuer) : "")+"\",\"issuerName\":\""+(!string.IsNullOrEmpty(issuer?.Nickname) && !(issuer?.IsServer ?? false) ? issuer.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"") : "")+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerCheaterReport)]
        internal void OnReportingCheater(Player issuer, Player target, string reason)
        {
            if (!(SCPStats.Singleton?.Config?.ModerationLogging ?? true) || target?.UserId == null || target.IsServer || !target.IsReady || Helper.IsPlayerNPC(target)) return;

            WebsocketHandler.SendRequest(RequestType.AddWarning, "{\"type\":\"7\",\"playerId\":\""+Helper.HandleId(target.UserId)+"\",\"message\":\""+reason.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"playerName\":\""+target.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"issuer\":\""+(!string.IsNullOrEmpty(issuer?.UserId) && !(issuer?.IsServer ?? false) ? Helper.HandleId(issuer) : "")+"\",\"issuerName\":\""+(!string.IsNullOrEmpty(issuer?.Nickname) && !(issuer?.IsServer ?? false) ? issuer.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"") : "")+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerReport)]
        internal void OnReporting(Player issuer, Player target, string reason)
        {
            if (!(SCPStats.Singleton?.Config?.ModerationLogging ?? true) || target?.UserId == null || target.IsServer || !target.IsReady || Helper.IsPlayerNPC(target)) return;

            WebsocketHandler.SendRequest(RequestType.AddWarning, "{\"type\":\"8\",\"playerId\":\""+Helper.HandleId(target.UserId)+"\",\"message\":\""+reason.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"playerName\":\""+target.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"")+"\",\"issuer\":\""+(!string.IsNullOrEmpty(issuer?.UserId) && !(issuer?.IsServer ?? false) ? Helper.HandleId(issuer) : "")+"\",\"issuerName\":\""+(!string.IsNullOrEmpty(issuer?.Nickname) && !(issuer?.IsServer ?? false) ? issuer.Nickname.Replace("\\", "\\\\").Replace("\"", "\\\"") : "")+"\"}");
        }

        [PluginEvent(ServerEventType.Scp049ResurrectBody)]
        internal void OnRecalling(Player scp049, Player target, BasicRagdoll body)
        {
            if (!Helper.IsRoundRunning()) return;
            
            var playerInfo = Helper.GetPlayerInfo(target, true, false);
            var scp049Info = Helper.GetPlayerInfo(scp049, true, false);

            if (playerInfo.PlayerID == scp049Info.PlayerID) scp049Info.PlayerID = null;
            if (playerInfo.PlayerID == null && scp049Info.PlayerID == null) return;

            WebsocketHandler.SendRequest(RequestType.Revive, "{\"playerid\":\""+playerInfo.PlayerID+"\",\"scp049\":\""+scp049Info.PlayerID+"\"}");
        }

        [PluginEvent(ServerEventType.PlayerCheckReservedSlot)]
        internal PlayerCheckReservedSlotCancellationData OnReservedSlotCheck(string userId, bool hasReservedSlot)
        {
            var id = Helper.HandleId(userId);
            
            // Reserved slot checking is handled as follows:
            // If the player has a reserved slot, let them through.
            // If they don't, check if they have an SCPStats reserved slot.
            // If they don't have data yet, let them through. Then, preauth will delay them until they get data.
            // Once they get data, they'll end up back here.
            if(hasReservedSlot) return PlayerCheckReservedSlotCancellationData.Override(true);
            else if (UserInfo.TryGetValue(id, out var userInfo) && userInfo.Item2 != null &&
                     userInfo.Item1.HasValue)
            {
                // They have info.
                return PlayerCheckReservedSlotCancellationData.Override(WebsocketRequests.HandleReservedSlots(userInfo.Item2, userInfo.Item1.Value));
            }
            else
            {
                // They don't have info. Let them through temporarily.
                return PlayerCheckReservedSlotCancellationData.Override(true);
            }
        }

        [PluginEvent(ServerEventType.PlayerPreauth)]
        internal PreauthCancellationData OnPreauth(string userId, string ipAddress, long expiration, CentralAuthPreauthFlags centralFlags, string region, byte[] signature, ConnectionRequest connectionRequest, int readerStartPosition)
        {
            var id = Helper.HandleId(userId);
            
            // We only *need* to do delays if a system like bans, reserved slots, or whitelist depends on it.
            var delayNeeded = (SCPStats.Singleton?.Config?.SyncBans ?? false) || Config.WhitelistEnabled() ||
                              (SCPStats.Singleton?.Config?.ReservedSlots?.Count(req => req != "DiscordRoleID") ?? 0) > 0;
            
            // If we have their info, no need to do anything.
            if(UserInfo.TryGetValue(id, out var userInfo) && userInfo.Item2 != null && userInfo.Item1.HasValue) return PreauthCancellationData.Accept();

            //If they haven't been pre-requested (such as at round end), request their info.
            if (!PreRequestedIDs.Contains(id))
            {
                if (UserInfo.Count > 500) UserInfo.Remove(UserInfo.Keys.First());
                UserInfo[id] = new Tuple<CentralAuthPreauthFlags?, UserInfoData>(centralFlags, null);
                WebsocketHandler.SendRequest(RequestType.UserInfo, Helper.UserInfoData(id, connectionRequest.RemoteEndPoint.Address.ToString().Trim().ToLower()));
            }
            
            // Now, we can delay them (if needed).
            if(delayNeeded) return PreauthCancellationData.RejectDelay(4, true);
            
            // TODO: More advanced usage (with DelayedIDs and FirstRoundPreauthDelay).
            
            // Accept the request as nothing else came up.
            return PreauthCancellationData.Accept();
        }

        internal static IEnumerator<float> UpdateLocalBanCache()
        {
            if(!(SCPStats.Singleton?.Config?.SyncBans ?? false)) yield break;

            yield return Timing.WaitForSeconds(5f);

            WebsocketHandler.SendRequest(RequestType.GetAllBans);
        }

        internal static void SetLocalBanCache(string info, bool write = true)
        {
            if(!(SCPStats.Singleton?.Config?.SyncBans ?? false) || string.IsNullOrEmpty(info)) return;

            var bans = info.Split('`');

            //First, we'll save our bans in the dictionary.
            //We're on a single thread, so clearing is safe.
            LocalBanCache.Clear();

            foreach (string ban in bans)
            {
                var banInfo = ban.Split(',');
                var bannedUser = banInfo[0];
                var banExpiry = Int64.Parse(banInfo[1], NumberStyles.Integer, Helper.UsCulture);

                LocalBanCache[bannedUser] = banExpiry;
            }

            if (!write) return;
            
            //Now, we should write it to a file. We'll place the file inside
            //of our config directory.
            var file = Path.Combine(PluginHandler.Get(SCPStats.Singleton).PluginDirectoryPath, "Bans.txt");
            
            File.WriteAllText(file, info);
        }

        internal static void LoadLocalBanCache()
        {
            if(!(SCPStats.Singleton?.Config?.SyncBans ?? false)) return;
            
            var file = Path.Combine(PluginHandler.Get(SCPStats.Singleton).PluginDirectoryPath, "Bans.txt");

            if (!File.Exists(file)) return;

            SetLocalBanCache(File.ReadAllText(file), false);
        }
    }
}
