// This file is part of the DSharpPlus project.
//
// Copyright (c) 2015 Mike Santiago
// Copyright (c) 2016-2021 DSharpPlus Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net.Abstractions;
using DSharpPlus.Net.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DSharpPlus
{
    public sealed partial class DiscordClient
    {
        #region Private Fields

        private string _sessionId;
        private bool _guildDownloadCompleted = false;

        #endregion

        #region Dispatch Handler

        internal async Task HandleDispatchAsync(GatewayPayload payload)
        {
            if (payload.Data is not JObject data)
            {
                this.Logger.LogWarning(LoggerEvents.WebSocketReceive, "Invalid payload body (this message is probably safe to ignore); opcode: {0} event: {1}; payload: {2}", payload.OpCode, payload.EventName, payload.Data);
                return;
            }

            DiscordChannel discordChannel;
            ulong guildId;
            ulong channelId;
            TransportUser transportUser = default;
            TransportMember transportMember = default;
            TransportUser referencedTransportUser = default;
            TransportMember referencedTransportMember = default;
            JToken rawMember = default;
            var rawReferencedMessage = data["referenced_message"];

            switch (payload.EventName.ToLowerInvariant())
            {
                #region Gateway Status

                case "ready":
                    var guilds = (JArray)data["guilds"];
                    var privateChannels = (JArray)data["private_channels"];
                    await this.OnReadyEventAsync(data.ToObject<ReadyPayload>(), guilds, privateChannels).ConfigureAwait(false);
                    break;

                case "resumed":
                    await this.OnResumedAsync().ConfigureAwait(false);
                    break;

                #endregion

                #region Channel

                case "channel_create":
                    discordChannel = data.ToObject<DiscordChannel>();
                    await this.OnChannelCreateEventAsync(discordChannel).ConfigureAwait(false);
                    break;

                case "channel_update":
                    await this.OnChannelUpdateEventAsync(data.ToObject<DiscordChannel>()).ConfigureAwait(false);
                    break;

                case "channel_delete":
                    discordChannel = data.ToObject<DiscordChannel>();
                    await this.OnChannelDeleteEventAsync(discordChannel.IsPrivate ? data.ToObject<DiscordDmChannel>() : discordChannel).ConfigureAwait(false);
                    break;

                case "channel_pins_update":
                    channelId = (ulong)data["channel_id"];
                    var timestamp = (string)data["last_pin_timestamp"];
                    await this.OnChannelPinsUpdateAsync((ulong?)data["guild_id"], this.InternalGetCachedChannel(channelId), timestamp != null ? DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture) : default(DateTimeOffset?)).ConfigureAwait(false);
                    break;

                #endregion

                #region Guild

                case "guild_create":
                    await this.OnGuildCreateEventAsync(data.ToDiscordObject<DiscordGuild>(), (JArray)data["members"], data["presences"].ToDiscordObject<IEnumerable<DiscordPresence>>()).ConfigureAwait(false);
                    break;

                case "guild_update":
                    await this.OnGuildUpdateEventAsync(data.ToDiscordObject<DiscordGuild>(), (JArray)data["members"]).ConfigureAwait(false);
                    break;

                case "guild_delete":
                    await this.OnGuildDeleteEventAsync(data.ToDiscordObject<DiscordGuild>(), (JArray)data["members"]).ConfigureAwait(false);
                    break;

                case "guild_sync":
                    guildId = (ulong)data["id"];
                    await this.OnGuildSyncEventAsync(this._guilds[guildId], (bool)data["large"], (JArray)data["members"], data["presences"].ToObject<IEnumerable<DiscordPresence>>()).ConfigureAwait(false);
                    break;

                case "guild_emojis_update":
                    guildId = (ulong)data["guild_id"];
                    var emojis = data["emojis"].ToObject<IEnumerable<DiscordEmoji>>();
                    await this.OnGuildEmojisUpdateEventAsync(this._guilds[guildId], emojis).ConfigureAwait(false);
                    break;

                case "guild_integrations_update":
                    guildId = (ulong)data["guild_id"];

                    // discord fires this event inconsistently if the current user leaves a guild.
                    if (!this._guilds.ContainsKey(guildId))
                        return;

                    await this.OnGuildIntegrationsUpdateEventAsync(this._guilds[guildId]).ConfigureAwait(false);
                    break;

                #endregion

                #region Guild Ban

                case "guild_ban_add":
                    transportUser = data["user"].ToObject<TransportUser>();
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildBanAddEventAsync(transportUser, this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_ban_remove":
                    transportUser = data["user"].ToObject<TransportUser>();
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildBanRemoveEventAsync(transportUser, this._guilds[guildId]).ConfigureAwait(false);
                    break;

                #endregion

                #region Guild Member

                case "guild_member_add":
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildMemberAddEventAsync(data.ToObject<TransportMember>(), this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_member_remove":
                    guildId = (ulong)data["guild_id"];
                    transportUser = data["user"].ToObject<TransportUser>();

                    if (!this._guilds.ContainsKey(guildId))
                    {
                        // discord fires this event inconsistently if the current user leaves a guild.
                        if (transportUser.Id != this.CurrentUser.Id)
                            this.Logger.LogError(LoggerEvents.WebSocketReceive, "Could not find {0} in guild cache", guildId);
                        return;
                    }

                    await this.OnGuildMemberRemoveEventAsync(transportUser, this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_member_update":
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildMemberUpdateEventAsync(data.ToDiscordObject<TransportMember>(), this._guilds[guildId], data["roles"].ToObject<IEnumerable<ulong>>(), (string)data["nick"], (bool?)data["pending"]).ConfigureAwait(false);
                    break;

                case "guild_members_chunk":
                    await this.OnGuildMembersChunkEventAsync(data).ConfigureAwait(false);
                    break;

                #endregion

                #region Guild Role

                case "guild_role_create":
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildRoleCreateEventAsync(data["role"].ToObject<DiscordRole>(), this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_role_update":
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildRoleUpdateEventAsync(data["role"].ToObject<DiscordRole>(), this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_role_delete":
                    guildId = (ulong)data["guild_id"];
                    await this.OnGuildRoleDeleteEventAsync((ulong)data["role_id"], this._guilds[guildId]).ConfigureAwait(false);
                    break;

                #endregion

                #region Invite

                case "invite_create":
                    guildId = (ulong)data["guild_id"];
                    channelId = (ulong)data["channel_id"];
                    await this.OnInviteCreateEventAsync(channelId, guildId, data.ToObject<DiscordInvite>()).ConfigureAwait(false);
                    break;

                case "invite_delete":
                    guildId = (ulong)data["guild_id"];
                    channelId = (ulong)data["channel_id"];
                    await this.OnInviteDeleteEventAsync(channelId, guildId, data).ConfigureAwait(false);
                    break;

                #endregion

                #region Message

                case "message_ack":
                    channelId = (ulong)data["channel_id"];
                    var messageId = (ulong)data["message_id"];
                    await this.OnMessageAckEventAsync(this.InternalGetCachedChannel(channelId), messageId).ConfigureAwait(false);
                    break;

                case "message_create":
                    rawMember = data["member"];

                    if (rawMember != null)
                        transportMember = rawMember.ToObject<TransportMember>();

                    if (rawReferencedMessage != null && rawReferencedMessage.HasValues)
                    {
                        if (rawReferencedMessage.SelectToken("author") != null)
                        {
                            referencedTransportUser = rawReferencedMessage.SelectToken("author").ToObject<TransportUser>();
                        }

                        if (rawReferencedMessage.SelectToken("member") != null)
                        {
                            referencedTransportMember = rawReferencedMessage.SelectToken("member").ToObject<TransportMember>();
                        }
                    }

                    await this.OnMessageCreateEventAsync(data.ToDiscordObject<DiscordMessage>(), data["author"].ToObject<TransportUser>(), transportMember, referencedTransportUser, referencedTransportMember).ConfigureAwait(false);
                    break;

                case "message_update":
                    rawMember = data["member"];

                    if (rawMember != null)
                        transportMember = rawMember.ToObject<TransportMember>();

                    if (rawReferencedMessage != null && rawReferencedMessage.HasValues)
                    {
                        if (rawReferencedMessage.SelectToken("author") != null)
                        {
                            referencedTransportUser = rawReferencedMessage.SelectToken("author").ToObject<TransportUser>();
                        }

                        if (rawReferencedMessage.SelectToken("member") != null)
                        {
                            referencedTransportMember = rawReferencedMessage.SelectToken("member").ToObject<TransportMember>();
                        }
                    }

                    await this.OnMessageUpdateEventAsync(data.ToDiscordObject<DiscordMessage>(), data["author"]?.ToObject<TransportUser>(), transportMember, referencedTransportUser, referencedTransportMember).ConfigureAwait(false);
                    break;

                // delete event does *not* include message object
                case "message_delete":
                    await this.OnMessageDeleteEventAsync((ulong)data["id"], (ulong)data["channel_id"], (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "message_delete_bulk":
                    await this.OnMessageBulkDeleteEventAsync(data["ids"].ToObject<ulong[]>(), (ulong)data["channel_id"], (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                #endregion

                #region Message Reaction

                case "message_reaction_add":
                    rawMember = data["member"];

                    if (rawMember != null)
                        transportMember = rawMember.ToObject<TransportMember>();

                    await this.OnMessageReactionAddAsync((ulong)data["user_id"], (ulong)data["message_id"], (ulong)data["channel_id"], (ulong?)data["guild_id"], transportMember, data["emoji"].ToObject<DiscordEmoji>()).ConfigureAwait(false);
                    break;

                case "message_reaction_remove":
                    await this.OnMessageReactionRemoveAsync((ulong)data["user_id"], (ulong)data["message_id"], (ulong)data["channel_id"], (ulong?)data["guild_id"], data["emoji"].ToObject<DiscordEmoji>()).ConfigureAwait(false);
                    break;

                case "message_reaction_remove_all":
                    await this.OnMessageReactionRemoveAllAsync((ulong)data["message_id"], (ulong)data["channel_id"], (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "message_reaction_remove_emoji":
                    await this.OnMessageReactionRemoveEmojiAsync((ulong)data["message_id"], (ulong)data["channel_id"], (ulong)data["guild_id"], data["emoji"]).ConfigureAwait(false);
                    break;

                #endregion

                #region User/Presence Update

                case "presence_update":
                    await this.OnPresenceUpdateEventAsync(data, (JObject)data["user"]).ConfigureAwait(false);
                    break;

                case "user_settings_update":
                    await this.OnUserSettingsUpdateEventAsync(data.ToObject<TransportUser>()).ConfigureAwait(false);
                    break;

                case "user_update":
                    await this.OnUserUpdateEventAsync(data.ToObject<TransportUser>()).ConfigureAwait(false);
                    break;

                #endregion

                #region Voice

                case "voice_state_update":
                    await this.OnVoiceStateUpdateEventAsync(data).ConfigureAwait(false);
                    break;

                case "voice_server_update":
                    guildId = (ulong)data["guild_id"];
                    await this.OnVoiceServerUpdateEventAsync((string)data["endpoint"], (string)data["token"], this._guilds[guildId]).ConfigureAwait(false);
                    break;

                #endregion

                #region Interaction/Integration/Application

                case "interaction_create":

                    rawMember = data["member"];

                    if (rawMember != null)
                    {
                        transportMember = data["member"].ToObject<TransportMember>();
                        transportUser = transportMember.User;
                    }
                    else
                    {
                        transportUser = data["user"].ToObject<TransportUser>();
                    }

                    channelId = (ulong)data["channel_id"];
                    await this.OnInteractionCreateAsync((ulong?)data["guild_id"], channelId, transportUser, transportMember, data.ToDiscordObject<DiscordInteraction>()).ConfigureAwait(false);
                    break;

                case "application_command_create":
                    await this.OnApplicationCommandCreateAsync(data.ToObject<DiscordApplicationCommand>(), (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "application_command_update":
                    await this.OnApplicationCommandUpdateAsync(data.ToObject<DiscordApplicationCommand>(), (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "application_command_delete":
                    await this.OnApplicationCommandDeleteAsync(data.ToObject<DiscordApplicationCommand>(), (ulong?)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "integration_create":
                    await this.OnIntegrationCreateAsync(data.ToObject<DiscordIntegration>(), (ulong)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "integration_update":
                    await this.OnIntegrationUpdateAsync(data.ToObject<DiscordIntegration>(), (ulong)data["guild_id"]).ConfigureAwait(false);
                    break;

                case "integration_delete":
                    await this.OnIntegrationDeleteAsync((ulong)data["id"], (ulong)data["guild_id"], (ulong?)data["application_id"]).ConfigureAwait(false);
                    break;

                #endregion

                #region Stage Instance

                case "stage_instance_create":
                    await this.OnStageInstanceCreateAsync(dat.ToObject<DiscordStageInstance>());
                    break;

                case "stage_instance_update":
                    await this.OnStageInstanceUpdateAsync(dat.ToObject<DiscordStageInstance>());
                    break;

                case "stage_instance_delete":
                    await this.OnStageInstanceDeleteAsync(dat.ToObject<DiscordStageInstance>());
                    break;

                #endregion

                #region Misc

                case "gift_code_update": //Not supposed to be dispatched to bots
                    break;

                case "typing_start":
                    channelId = (ulong)data["channel_id"];
                    rawMember = data["member"];

                    if (rawMember != null)
                        transportMember = rawMember.ToObject<TransportMember>();

                    await this.OnTypingStartEventAsync((ulong)data["user_id"], channelId, this.InternalGetCachedChannel(channelId), (ulong?)data["guild_id"], Utilities.GetDateTimeOffset((long)data["timestamp"]), transportMember).ConfigureAwait(false);
                    break;

                case "webhooks_update":
                    guildId = (ulong)data["guild_id"];
                    channelId = (ulong)data["channel_id"];
                    await this.OnWebhooksUpdateAsync(this._guilds[guildId].GetChannel(channelId), this._guilds[guildId]).ConfigureAwait(false);
                    break;

                case "guild_stickers_update":
                    var stickers = data["stickers"].ToDiscordObject<IEnumerable<DiscordMessageSticker>>();
                    await this.OnStickersUpdatedAsync(stickers, data).ConfigureAwait(false);
                    break;

                default:
                    await this.OnUnknownEventAsync(payload).ConfigureAwait(false);
                    this.Logger.LogWarning(LoggerEvents.WebSocketReceive, "Unknown event: {0}\npayload: {1}", payload.EventName, payload.Data);
                    break;

                    #endregion
            }
        }

        #endregion

        #region Events

        #region Gateway

        internal async Task OnReadyEventAsync(ReadyPayload ready, JArray rawGuilds, JArray rawDmChannels)
        {
            //ready.CurrentUser.Discord = this;

            var currentReadyUser = ready.CurrentUser;
            this.CurrentUser.Username = currentReadyUser.Username;
            this.CurrentUser.Discriminator = currentReadyUser.Discriminator;
            this.CurrentUser.AvatarHash = currentReadyUser.AvatarHash;
            this.CurrentUser.MfaEnabled = currentReadyUser.MfaEnabled;
            this.CurrentUser.Verified = currentReadyUser.Verified;
            this.CurrentUser.IsBot = currentReadyUser.IsBot;

            this.GatewayVersion = ready.GatewayVersion;
            this._sessionId = ready.SessionId;
            var rawGuildIdIndex = rawGuilds.ToDictionary(guild => (ulong)guild["id"], guild => (JObject)guild);

            this._privateChannels.Clear();
            foreach (var rawChannel in rawDmChannels)
            {
                var channel = rawChannel.ToObject<DiscordDmChannel>();

                channel.Discord = this;

                var rawRecipients = rawChannel["recipients"].ToObject<IEnumerable<TransportUser>>();
                var recipients = new List<DiscordUser>();
                foreach (var rawRecipient in rawRecipients)
                {
                    var user = new DiscordUser(rawRecipient) { Discord = this };
                    user = this.UpdateUserCache(user);

                    recipients.Add(user);
                }
                channel.Recipients = recipients;

                this._privateChannels[channel.Id] = channel;
            }

            this._guilds.Clear();
            foreach (var guild in ready.Guilds)
            {
                guild.Discord = this;

                if (guild._channels == null)
                    guild._channels = new ConcurrentDictionary<ulong, DiscordChannel>();

                foreach (var channel in guild.Channels.Values)
                {
                    channel.GuildId = guild.Id;
                    channel.Discord = this;
                    foreach (var permissionOverwrite in channel._permissionOverwrites)
                    {
                        permissionOverwrite.Discord = this;
                        permissionOverwrite._channel_id = channel.Id;
                    }
                }

                if (guild._roles == null)
                    guild._roles = new ConcurrentDictionary<ulong, DiscordRole>();

                foreach (var role in guild.Roles.Values)
                {
                    role.Discord = this;
                    role._guildId = guild.Id;
                }

                var rawGuild = rawGuildIdIndex[guild.Id];
                var rawMembers = (JArray)rawGuild["members"];

                if (guild._members != null)
                    guild._members.Clear();
                else
                    guild._members = new ConcurrentDictionary<ulong, DiscordMember>();

                if (rawMembers != null)
                {
                    foreach (var member in rawMembers)
                    {
                        var transportMember = member.ToObject<TransportMember>();

                        var user = new DiscordUser(transportMember.User) { Discord = this };
                        user = this.UpdateUserCache(user);

                        guild._members[transportMember.User.Id] = new DiscordMember(transportMember) { Discord = this, _guildId = guild.Id };
                    }
                }

                if (guild._emojis == null)
                    guild._emojis = new ConcurrentDictionary<ulong, DiscordEmoji>();

                foreach (var emoji in guild.Emojis.Values)
                    emoji.Discord = this;

                if (guild._voiceStates == null)
                    guild._voiceStates = new ConcurrentDictionary<ulong, DiscordVoiceState>();

                foreach (var voiceStates in guild.VoiceStates.Values)
                    voiceStates.Discord = this;

                this._guilds[guild.Id] = guild;
            }

            await this._ready.InvokeAsync(this, new ReadyEventArgs()).ConfigureAwait(false);
        }

        internal Task OnResumedAsync()
        {
            this.Logger.LogInformation(LoggerEvents.SessionUpdate, "Session resumed");
            return this._resumed.InvokeAsync(this, new ReadyEventArgs());
        }

        #endregion

        #region Channel

        internal async Task OnChannelCreateEventAsync(DiscordChannel channel)
        {
            channel.Discord = this;
            foreach (var permissionOverwrite in channel._permissionOverwrites)
            {
                permissionOverwrite.Discord = this;
                permissionOverwrite._channel_id = channel.Id;
            }

            this._guilds[channel.GuildId.Value]._channels[channel.Id] = channel;

            await this._channelCreated.InvokeAsync(this, new ChannelCreateEventArgs { Channel = channel, Guild = channel.Guild }).ConfigureAwait(false);
        }

        internal async Task OnChannelUpdateEventAsync(DiscordChannel channel)
        {
            if (channel == null)
                return;

            channel.Discord = this;

            var guildId = channel.Guild;

            var newChannel = this.InternalGetCachedChannel(channel.Id);
            DiscordChannel oldChannel = null;

            if (newChannel != null)
            {
                oldChannel = new DiscordChannel
                {
                    Bitrate = newChannel.Bitrate,
                    Discord = this,
                    GuildId = newChannel.GuildId,
                    Id = newChannel.Id,
                    LastMessageId = newChannel.LastMessageId,
                    Name = newChannel.Name,
                    _permissionOverwrites = new List<DiscordOverwrite>(newChannel._permissionOverwrites),
                    Position = newChannel.Position,
                    Topic = newChannel.Topic,
                    Type = newChannel.Type,
                    UserLimit = newChannel.UserLimit,
                    ParentId = newChannel.ParentId,
                    IsNSFW = newChannel.IsNSFW,
                    PerUserRateLimit = newChannel.PerUserRateLimit,
                    RtcRegionId = newChannel.RtcRegionId,
                    QualityMode = newChannel.QualityMode
                };

                newChannel.Bitrate = channel.Bitrate;
                newChannel.Name = channel.Name;
                newChannel.Position = channel.Position;
                newChannel.Topic = channel.Topic;
                newChannel.UserLimit = channel.UserLimit;
                newChannel.ParentId = channel.ParentId;
                newChannel.IsNSFW = channel.IsNSFW;
                newChannel.PerUserRateLimit = channel.PerUserRateLimit;
                newChannel.Type = channel.Type;
                newChannel.RtcRegionId = channel.RtcRegionId;
                newChannel.QualityMode = channel.QualityMode;

                newChannel._permissionOverwrites.Clear();

                foreach (var permissionOverwrite in channel._permissionOverwrites)
                {
                    permissionOverwrite.Discord = this;
                    permissionOverwrite._channel_id = channel.Id;
                }

                newChannel._permissionOverwrites.AddRange(channel._permissionOverwrites);
            }
            else if (guildId != null)
            {
                guildId._channels[channel.Id] = channel;
            }

            await this._channelUpdated.InvokeAsync(this, new ChannelUpdateEventArgs { ChannelAfter = newChannel, Guild = guildId, ChannelBefore = oldChannel }).ConfigureAwait(false);
        }

        internal async Task OnChannelDeleteEventAsync(DiscordChannel channel)
        {
            if (channel == null)
                return;

            channel.Discord = this;

            if (channel.Type == ChannelType.Group || channel.Type == ChannelType.Private)
            {
                var dmChannel = channel as DiscordDmChannel;

                _ = this._privateChannels.TryRemove(dmChannel.Id, out _);

                await this._dmChannelDeleted.InvokeAsync(this, new DmChannelDeleteEventArgs { Channel = dmChannel }).ConfigureAwait(false);
            }
            else
            {
                var guildId = channel.Guild;

                if (guildId._channels.TryRemove(channel.Id, out var cachedChannel)) channel = cachedChannel;

                await this._channelDeleted.InvokeAsync(this, new ChannelDeleteEventArgs { Channel = channel, Guild = guildId }).ConfigureAwait(false);
            }
        }

        internal async Task OnChannelPinsUpdateAsync(ulong? guildId, DiscordChannel channel, DateTimeOffset? lastPinTimestamp)
        {
            if (channel == null)
                return;

            var guild = this.InternalGetCachedGuild(guildId);

            var eventArgs = new ChannelPinsUpdateEventArgs
            {
                Guild = guild,
                Channel = channel,
                LastPinTimestamp = lastPinTimestamp
            };
            await this._channelPinsUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Guild

        internal async Task OnGuildCreateEventAsync(DiscordGuild guild, JArray rawMembers, IEnumerable<DiscordPresence> presences)
        {
            if (presences != null)
            {
                foreach (var presence in presences)
                {
                    presence.Discord = this;
                    presence.GuildId = guild.Id;
                    presence.Activity = new DiscordActivity(presence.RawActivity);
                    if (presence.RawActivities != null)
                    {
                        presence.InternalActivities = new DiscordActivity[presence.RawActivities.Length];
                        for (var i = 0; i < presence.RawActivities.Length; i++)
                            presence.InternalActivities[i] = new DiscordActivity(presence.RawActivities[i]);
                    }
                    this._presences[presence.InternalUser.Id] = presence;
                }
            }

            var exists = this._guilds.TryGetValue(guild.Id, out var foundGuild);

            guild.Discord = this;
            guild.IsUnavailable = false;
            var eventGuild = guild;
            if (exists)
                guild = foundGuild;

            if (guild._channels == null)
                guild._channels = new ConcurrentDictionary<ulong, DiscordChannel>();
            if (guild._roles == null)
                guild._roles = new ConcurrentDictionary<ulong, DiscordRole>();
            if (guild._emojis == null)
                guild._emojis = new ConcurrentDictionary<ulong, DiscordEmoji>();
            if (guild._stickers == null)
                guild._stickers = new ConcurrentDictionary<ulong, DiscordMessageSticker>();
            if (guild._voiceStates == null)
                guild._voiceStates = new ConcurrentDictionary<ulong, DiscordVoiceState>();
            if (guild._members == null)
                guild._members = new ConcurrentDictionary<ulong, DiscordMember>();
            if (guild._stageInstances == null)
                guild._stageInstances = new ConcurrentDictionary<ulong, DiscordStageInstance>();

            this.UpdateCachedGuild(eventGuild, rawMembers);

            guild.JoinedAt = eventGuild.JoinedAt;
            guild.IsLarge = eventGuild.IsLarge;
            guild.MemberCount = Math.Max(eventGuild.MemberCount, guild._members.Count);
            guild.IsUnavailable = eventGuild.IsUnavailable;
            guild.PremiumSubscriptionCount = eventGuild.PremiumSubscriptionCount;
            guild.PremiumTier = eventGuild.PremiumTier;
            guild.Banner = eventGuild.Banner;
            guild.VanityUrlCode = eventGuild.VanityUrlCode;
            guild.Description = eventGuild.Description;
            guild.IsNSFW = eventGuild.IsNSFW;

            foreach (var voiceState in eventGuild._voiceStates) guild._voiceStates[voiceState.Key] = voiceState.Value;

            foreach (var channel in guild._channels.Values)
            {
                channel.GuildId = guild.Id;
                channel.Discord = this;
                foreach (var permissionOverwrite in channel._permissionOverwrites)
                {
                    permissionOverwrite.Discord = this;
                    permissionOverwrite._channel_id = channel.Id;
                }
            }
            foreach (var emoji in guild._emojis.Values)
                emoji.Discord = this;
            foreach (var sticker in guild._stickers.Values)
                sticker.Discord = this;
            foreach (var voiceState in guild._voiceStates.Values)
                voiceState.Discord = this;
            foreach (var role in guild._roles.Values)
            {
                role.Discord = this;
                role._guildId = guild.Id;
            }
            foreach (var instance in guild._stageInstances.Values)
                instance.Discord = this;

            var old = Volatile.Read(ref this._guildDownloadCompleted);
            var downloadComplete = this._guilds.Values.All(xg => !xg.IsUnavailable);
            Volatile.Write(ref this._guildDownloadCompleted, downloadComplete);

            if (exists)
                await this._guildAvailable.InvokeAsync(this, new GuildCreateEventArgs { Guild = guild }).ConfigureAwait(false);
            else
                await this._guildCreated.InvokeAsync(this, new GuildCreateEventArgs { Guild = guild }).ConfigureAwait(false);

            if (downloadComplete && !old)
                await this._guildDownloadCompletedEv.InvokeAsync(this, new GuildDownloadCompletedEventArgs(this.Guilds)).ConfigureAwait(false);
        }

        internal async Task OnGuildUpdateEventAsync(DiscordGuild guild, JArray rawMembers)
        {
            DiscordGuild oldGuild;

            if (!this._guilds.ContainsKey(guild.Id))
            {
                this._guilds[guild.Id] = guild;
                oldGuild = null;
            }
            else
            {
                var guildById = this._guilds[guild.Id];

                oldGuild = new DiscordGuild
                {
                    Discord = guildById.Discord,
                    Name = guildById.Name,
                    AfkChannelId = guildById.AfkChannelId,
                    AfkTimeout = guildById.AfkTimeout,
                    DefaultMessageNotifications = guildById.DefaultMessageNotifications,
                    ExplicitContentFilter = guildById.ExplicitContentFilter,
                    Features = guildById.Features,
                    IconHash = guildById.IconHash,
                    Id = guildById.Id,
                    IsLarge = guildById.IsLarge,
                    IsSynced = guildById.IsSynced,
                    IsUnavailable = guildById.IsUnavailable,
                    JoinedAt = guildById.JoinedAt,
                    MemberCount = guildById.MemberCount,
                    MaxMembers = guildById.MaxMembers,
                    MaxPresences = guildById.MaxPresences,
                    ApproximateMemberCount = guildById.ApproximateMemberCount,
                    ApproximatePresenceCount = guildById.ApproximatePresenceCount,
                    MaxVideoChannelUsers = guildById.MaxVideoChannelUsers,
                    DiscoverySplashHash = guildById.DiscoverySplashHash,
                    PreferredLocale = guildById.PreferredLocale,
                    MfaLevel = guildById.MfaLevel,
                    OwnerId = guildById.OwnerId,
                    SplashHash = guildById.SplashHash,
                    SystemChannelId = guildById.SystemChannelId,
                    SystemChannelFlags = guildById.SystemChannelFlags,
                    WidgetEnabled = guildById.WidgetEnabled,
                    WidgetChannelId = guildById.WidgetChannelId,
                    VerificationLevel = guildById.VerificationLevel,
                    RulesChannelId = guildById.RulesChannelId,
                    PublicUpdatesChannelId = guildById.PublicUpdatesChannelId,
                    VoiceRegionId = guildById.VoiceRegionId,
                    IsNSFW = guildById.IsNSFW,
                    _channels = new ConcurrentDictionary<ulong, DiscordChannel>(),
                    _emojis = new ConcurrentDictionary<ulong, DiscordEmoji>(),
                    _members = new ConcurrentDictionary<ulong, DiscordMember>(),
                    _roles = new ConcurrentDictionary<ulong, DiscordRole>(),
                    _voiceStates = new ConcurrentDictionary<ulong, DiscordVoiceState>()
                };

                foreach (var channel in guildById._channels) oldGuild._channels[channel.Key] = channel.Value;
                foreach (var emoji in guildById._emojis) oldGuild._emojis[emoji.Key] = emoji.Value;
                foreach (var role in guildById._roles) oldGuild._roles[role.Key] = role.Value;
                foreach (var voiceState in guildById._voiceStates) oldGuild._voiceStates[voiceState.Key] = voiceState.Value;
                foreach (var member in guildById._members) oldGuild._members[member.Key] = member.Value;
            }

            guild.Discord = this;
            guild.IsUnavailable = false;
            var eventGuild = guild;
            guild = this._guilds[eventGuild.Id];

            if (guild._channels == null)
                guild._channels = new ConcurrentDictionary<ulong, DiscordChannel>();
            if (guild._roles == null)
                guild._roles = new ConcurrentDictionary<ulong, DiscordRole>();
            if (guild._emojis == null)
                guild._emojis = new ConcurrentDictionary<ulong, DiscordEmoji>();
            if (guild._voiceStates == null)
                guild._voiceStates = new ConcurrentDictionary<ulong, DiscordVoiceState>();
            if (guild._members == null)
                guild._members = new ConcurrentDictionary<ulong, DiscordMember>();

            this.UpdateCachedGuild(eventGuild, rawMembers);

            foreach (var channel in guild._channels.Values)
            {
                channel.GuildId = guild.Id;
                channel.Discord = this;
                foreach (var permissionOverwrite in channel._permissionOverwrites)
                {
                    permissionOverwrite.Discord = this;
                    permissionOverwrite._channel_id = channel.Id;
                }
            }
            foreach (var emoji in guild._emojis.Values)
                emoji.Discord = this;
            foreach (var voiceState in guild._voiceStates.Values)
                voiceState.Discord = this;
            foreach (var role in guild._roles.Values)
            {
                role.Discord = this;
                role._guildId = guild.Id;
            }

            await this._guildUpdated.InvokeAsync(this, new GuildUpdateEventArgs { GuildBefore = oldGuild, GuildAfter = guild }).ConfigureAwait(false);
        }

        internal async Task OnGuildDeleteEventAsync(DiscordGuild guild, JArray rawMembers)
        {
            if (guild.IsUnavailable)
            {
                if (!this._guilds.TryGetValue(guild.Id, out var guildById))
                    return;

                guildById.IsUnavailable = true;

                await this._guildUnavailable.InvokeAsync(this, new GuildDeleteEventArgs { Guild = guild, Unavailable = true }).ConfigureAwait(false);
            }
            else
            {
                if (!this._guilds.TryRemove(guild.Id, out var removedGuild))
                    return;

                await this._guildDeleted.InvokeAsync(this, new GuildDeleteEventArgs { Guild = removedGuild }).ConfigureAwait(false);
            }
        }

        internal async Task OnGuildSyncEventAsync(DiscordGuild guild, bool isLarge, JArray rawMembers, IEnumerable<DiscordPresence> presences)
        {
            presences = presences.Select(presence => { presence.Discord = this; presence.Activity = new DiscordActivity(presence.RawActivity); return presence; });
            foreach (var presence in presences)
                this._presences[presence.InternalUser.Id] = presence;

            guild.IsSynced = true;
            guild.IsLarge = isLarge;

            this.UpdateCachedGuild(guild, rawMembers);

            await this._guildAvailable.InvokeAsync(this, new GuildCreateEventArgs { Guild = guild }).ConfigureAwait(false);
        }

        internal async Task OnGuildEmojisUpdateEventAsync(DiscordGuild guild, IEnumerable<DiscordEmoji> newEmojis)
        {
            var oldEmojis = new ConcurrentDictionary<ulong, DiscordEmoji>(guild._emojis);
            guild._emojis.Clear();

            foreach (var emoji in newEmojis)
            {
                emoji.Discord = this;
                guild._emojis[emoji.Id] = emoji;
            }

            var eventArgs = new GuildEmojisUpdateEventArgs
            {
                Guild = guild,
                EmojisAfter = guild.Emojis,
                EmojisBefore = oldEmojis
            };
            await this._guildEmojisUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildIntegrationsUpdateEventAsync(DiscordGuild guild)
        {
            var eventArgs = new GuildIntegrationsUpdateEventArgs
            {
                Guild = guild
            };
            await this._guildIntegrationsUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Guild Ban

        internal async Task OnGuildBanAddEventAsync(TransportUser user, DiscordGuild guild)
        {
            var newUser = new DiscordUser(user) { Discord = this };
            newUser = this.UpdateUserCache(newUser);

            if (!guild.Members.TryGetValue(user.Id, out var member))
                member = new DiscordMember(newUser) { Discord = this, _guildId = guild.Id };
            var eventArgs = new GuildBanAddEventArgs
            {
                Guild = guild,
                Member = member
            };
            await this._guildBanAdded.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildBanRemoveEventAsync(TransportUser user, DiscordGuild guild)
        {
            var newUser = new DiscordUser(user) { Discord = this };
            newUser = this.UpdateUserCache(newUser);

            if (!guild.Members.TryGetValue(user.Id, out var member))
                member = new DiscordMember(newUser) { Discord = this, _guildId = guild.Id };
            var eventArgs = new GuildBanRemoveEventArgs
            {
                Guild = guild,
                Member = member
            };
            await this._guildBanRemoved.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Guild Member

        internal async Task OnGuildMemberAddEventAsync(TransportMember member, DiscordGuild guild)
        {
            var newUser = new DiscordUser(member.User) { Discord = this };
            newUser = this.UpdateUserCache(newUser);

            var newMember = new DiscordMember(member)
            {
                Discord = this,
                _guildId = guild.Id
            };

            guild._members[newMember.Id] = newMember;
            guild.MemberCount++;

            var eventArgs = new GuildMemberAddEventArgs
            {
                Guild = guild,
                Member = newMember
            };
            await this._guildMemberAdded.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildMemberRemoveEventAsync(TransportUser user, DiscordGuild guild)
        {
            var newUser = new DiscordUser(user);

            if (!guild._members.TryRemove(user.Id, out var member))
                member = new DiscordMember(newUser) { Discord = this, _guildId = guild.Id };
            guild.MemberCount--;

            this.UpdateUserCache(newUser);

            var eventArgs = new GuildMemberRemoveEventArgs
            {
                Guild = guild,
                Member = member
            };
            await this._guildMemberRemoved.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildMemberUpdateEventAsync(TransportMember member, DiscordGuild guild, IEnumerable<ulong> roles, string nick, bool? pending)
        {
            var user = new DiscordUser(member.User) { Discord = this };
            user = this.UpdateUserCache(user);

            if (!guild.Members.TryGetValue(member.User.Id, out var memberByUserId))
                memberByUserId = new DiscordMember(user) { Discord = this, _guildId = guild.Id };

            var oldNick = memberByUserId.Nickname;
            var pendingOld = memberByUserId.IsPending;
            var oldRoles = new ReadOnlyCollection<DiscordRole>(new List<DiscordRole>(memberByUserId.Roles));

            memberByUserId._avatarHash = member.AvatarHash;
            memberByUserId.Nickname = nick;
            memberByUserId.IsPending = pending;
            memberByUserId._roleIds.Clear();
            memberByUserId._roleIds.AddRange(roles);

            var eventArgs = new GuildMemberUpdateEventArgs
            {
                Guild = guild,
                Member = memberByUserId,

                NicknameAfter = memberByUserId.Nickname,
                RolesAfter = new ReadOnlyCollection<DiscordRole>(new List<DiscordRole>(memberByUserId.Roles)),
                PendingAfter = memberByUserId.IsPending,

                NicknameBefore = oldNick,
                RolesBefore = oldRoles,
                PendingBefore = pendingOld,
            };
            await this._guildMemberUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildMembersChunkEventAsync(JObject data)
        {
            var guild = this.Guilds[(ulong)data["guild_id"]];
            var chunkIndex = (int)data["chunk_index"];
            var chunkCount = (int)data["chunk_count"];
            var nonce = (string)data["nonce"];

            var membersHashSet = new HashSet<DiscordMember>();
            var presenceHashSet = new HashSet<DiscordPresence>();

            var members = data["members"].ToObject<TransportMember[]>();

            for (var i = 0; i < members.Count(); i++)
            {
                var member = new DiscordMember(members[i]) { Discord = this, _guildId = guild.Id };

                if (!this.UserCache.ContainsKey(member.Id))
                    this.UserCache[member.Id] = new DiscordUser(members[i].User) { Discord = this };

                guild._members[member.Id] = member;

                membersHashSet.Add(member);
            }

            guild.MemberCount = guild._members.Count;

            var eventArgs = new GuildMembersChunkEventArgs
            {
                Guild = guild,
                Members = new ReadOnlySet<DiscordMember>(membersHashSet),
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Nonce = nonce,
            };

            if (data["presences"] != null)
            {
                var presences = data["presences"].ToObject<DiscordPresence[]>();

                for (var i = 0; i < presences.Count(); i++)
                {
                    var presence = presences[i];
                    presence.Discord = this;
                    presence.Activity = new DiscordActivity(presence.RawActivity);

                    if (presence.RawActivities != null)
                    {
                        presence.InternalActivities = new DiscordActivity[presence.RawActivities.Length];
                        for (var j = 0; j < presence.RawActivities.Length; j++)
                            presence.InternalActivities[j] = new DiscordActivity(presence.RawActivities[j]);
                    }

                    presenceHashSet.Add(presence);
                }

                eventArgs.Presences = new ReadOnlySet<DiscordPresence>(presenceHashSet);
            }

            if (data["not_found"] != null)
            {
                var notFoundSet = data["not_found"].ToObject<ISet<ulong>>();
                eventArgs.NotFound = new ReadOnlySet<ulong>(notFoundSet);
            }

            await this._guildMembersChunked.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Guild Role

        internal async Task OnGuildRoleCreateEventAsync(DiscordRole role, DiscordGuild guild)
        {
            role.Discord = this;
            role._guildId = guild.Id;

            guild._roles[role.Id] = role;

            var eventArgs = new GuildRoleCreateEventArgs
            {
                Guild = guild,
                Role = role
            };
            await this._guildRoleCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildRoleUpdateEventAsync(DiscordRole role, DiscordGuild guild)
        {
            var newRole = guild.GetRole(role.Id);
            var oldRole = new DiscordRole
            {
                _guildId = guild.Id,
                _color = newRole._color,
                Discord = this,
                IsHoisted = newRole.IsHoisted,
                Id = newRole.Id,
                IsManaged = newRole.IsManaged,
                IsMentionable = newRole.IsMentionable,
                Name = newRole.Name,
                Permissions = newRole.Permissions,
                Position = newRole.Position
            };

            newRole._guildId = guild.Id;
            newRole._color = role._color;
            newRole.IsHoisted = role.IsHoisted;
            newRole.IsManaged = role.IsManaged;
            newRole.IsMentionable = role.IsMentionable;
            newRole.Name = role.Name;
            newRole.Permissions = role.Permissions;
            newRole.Position = role.Position;

            var eventArgs = new GuildRoleUpdateEventArgs
            {
                Guild = guild,
                RoleAfter = newRole,
                RoleBefore = oldRole
            };
            await this._guildRoleUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnGuildRoleDeleteEventAsync(ulong roleId, DiscordGuild guild)
        {
            if (!guild._roles.TryRemove(roleId, out var role))
                this.Logger.LogWarning($"Attempted to delete a nonexistent role ({roleId}) from guild ({guild}).");

            var eventArgs = new GuildRoleDeleteEventArgs
            {
                Guild = guild,
                Role = role
            };
            await this._guildRoleDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Invite

        internal async Task OnInviteCreateEventAsync(ulong channelId, ulong guildId, DiscordInvite invite)
        {
            var guild = this.InternalGetCachedGuild(guildId);
            var channel = this.InternalGetCachedChannel(channelId);

            invite.Discord = this;

            guild._invites[invite.Code] = invite;

            var eventArgs = new InviteCreateEventArgs
            {
                Channel = channel,
                Guild = guild,
                Invite = invite
            };
            await this._inviteCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnInviteDeleteEventAsync(ulong channelId, ulong guildId, JToken data)
        {
            var guild = this.InternalGetCachedGuild(guildId);
            var channel = this.InternalGetCachedChannel(channelId);

            if (!guild._invites.TryRemove(data["code"].ToString(), out var invite))
            {
                invite = data.ToObject<DiscordInvite>();
                invite.Discord = this;
            }

            invite.IsRevoked = true;

            var eventArgs = new InviteDeleteEventArgs
            {
                Channel = channel,
                Guild = guild,
                Invite = invite
            };
            await this._inviteDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Message

        internal async Task OnMessageAckEventAsync(DiscordChannel channel, ulong messageId)
        {
            if (this.MessageCache == null || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channel.Id, out var message))
            {
                message = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channel.Id,
                    Discord = this,
                };
            }

            await this._messageAcknowledged.InvokeAsync(this, new MessageAcknowledgeEventArgs { Message = message }).ConfigureAwait(false);
        }

        internal async Task OnMessageCreateEventAsync(DiscordMessage message, TransportUser author, TransportMember member, TransportUser referenceAuthor, TransportMember referenceMember)
        {
            message.Discord = this;
            this.PopulateMessageReactionsAndCache(message, author, member);
            message.PopulateMentions();

            if (message.Channel == null && message.ChannelId == default)
                this.Logger.LogWarning(LoggerEvents.WebSocketReceive, "Channel which the last message belongs to is not in cache - cache state might be invalid!");

            if (message.ReferencedMessage != null)
            {
                message.ReferencedMessage.Discord = this;
                this.PopulateMessageReactionsAndCache(message.ReferencedMessage, referenceAuthor, referenceMember);
                message.ReferencedMessage.PopulateMentions();
            }

            foreach (var sticker in message.Stickers)
                sticker.Discord = this;

            var eventArgs = new MessageCreateEventArgs
            {
                Message = message,

                MentionedUsers = new ReadOnlyCollection<DiscordUser>(message._mentionedUsers),
                MentionedRoles = message._mentionedRoles != null ? new ReadOnlyCollection<DiscordRole>(message._mentionedRoles) : null,
                MentionedChannels = message._mentionedChannels != null ? new ReadOnlyCollection<DiscordChannel>(message._mentionedChannels) : null
            };
            await this._messageCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageUpdateEventAsync(DiscordMessage message, TransportUser author, TransportMember member, TransportUser referenceAuthor, TransportMember referenceMember)
        {
            DiscordGuild guild;

            message.Discord = this;
            var eventMessages = message;

            DiscordMessage oldMessage = null;
            if (this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == eventMessages.Id && cachedMessage.ChannelId == eventMessages.ChannelId, out message))
            {
                message = eventMessages;
                this.PopulateMessageReactionsAndCache(message, author, member);
                guild = message.Channel?.Guild;

                if (message.ReferencedMessage != null)
                {
                    message.ReferencedMessage.Discord = this;
                    this.PopulateMessageReactionsAndCache(message.ReferencedMessage, referenceAuthor, referenceMember);
                    message.ReferencedMessage.PopulateMentions();
                }
            }
            else
            {
                oldMessage = new DiscordMessage(message);

                guild = message.Channel?.Guild;
                message.EditedTimestampRaw = eventMessages.EditedTimestampRaw;
                if (eventMessages.Content != null)
                    message.Content = eventMessages.Content;
                message._embeds.Clear();
                message._embeds.AddRange(eventMessages._embeds);
                message._attachments.Clear();
                message._attachments.AddRange(eventMessages._attachments);
                message.Pinned = eventMessages.Pinned;
                message.IsTTS = eventMessages.IsTTS;
            }

            message.PopulateMentions();

            var eventArgs = new MessageUpdateEventArgs
            {
                Message = message,
                MessageBefore = oldMessage,
                MentionedUsers = new ReadOnlyCollection<DiscordUser>(message._mentionedUsers),
                MentionedRoles = message._mentionedRoles != null ? new ReadOnlyCollection<DiscordRole>(message._mentionedRoles) : null,
                MentionedChannels = message._mentionedChannels != null ? new ReadOnlyCollection<DiscordChannel>(message._mentionedChannels) : null
            };
            await this._messageUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageDeleteEventAsync(ulong messageId, ulong channelId, ulong? guildId)
        {
            var channel = this.InternalGetCachedChannel(channelId);
            var guild = this.InternalGetCachedGuild(guildId);

            if (channel == null
                || this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var mesage))
            {
                mesage = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channelId,
                    Discord = this,
                };
            }

            if (this.Configuration.MessageCacheSize > 0)
                this.MessageCache?.Remove(cachedMessage => cachedMessage.Id == mesage.Id && cachedMessage.ChannelId == channelId);

            var eventArgs = new MessageDeleteEventArgs
            {
                Channel = channel,
                Message = mesage,
                Guild = guild
            };
            await this._messageDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageBulkDeleteEventAsync(ulong[] messageIds, ulong channelId, ulong? guildId)
        {
            var channel = this.InternalGetCachedChannel(channelId);

            var messages = new List<DiscordMessage>(messageIds.Length);
            foreach (var messageId in messageIds)
            {
                if (channel == null
                    || this.Configuration.MessageCacheSize == 0
                    || this.MessageCache == null
                    || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var message))
                {
                    message = new DiscordMessage
                    {
                        Id = messageId,
                        ChannelId = channelId,
                        Discord = this,
                    };
                }
                if (this.Configuration.MessageCacheSize > 0)
                    this.MessageCache?.Remove(cachedMessage => cachedMessage.Id == message.Id && cachedMessage.ChannelId == channelId);
                messages.Add(message);
            }

            var guild = this.InternalGetCachedGuild(guildId);

            var eventArgs = new MessageBulkDeleteEventArgs
            {
                Channel = channel,
                Messages = new ReadOnlyCollection<DiscordMessage>(messages),
                Guild = guild
            };
            await this._messagesBulkDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Message Reaction

        internal async Task OnMessageReactionAddAsync(ulong userId, ulong messageId, ulong channelId, ulong? guildId, TransportMember transportMember, DiscordEmoji emoji)
        {
            var channel = this.InternalGetCachedChannel(channelId);
            var guild = this.InternalGetCachedGuild(guildId);
            emoji.Discord = this;

            var usr = this.UpdateUser(new DiscordUser { Id = userId, Discord = this }, guildId, guild, transportMember);

            if (channel == null
                || this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var message))
            {
                message = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channelId,
                    Discord = this,
                    _reactions = new List<DiscordReaction>()
                };
            }

            var reaction = message._reactions.FirstOrDefault(react => react.Emoji == emoji);
            if (reaction == null)
            {
                message._reactions.Add(reaction = new DiscordReaction
                {
                    Count = 1,
                    Emoji = emoji,
                    IsMe = this.CurrentUser.Id == userId
                });
            }
            else
            {
                reaction.Count++;
                reaction.IsMe |= this.CurrentUser.Id == userId;
            }

            var eventArgs = new MessageReactionAddEventArgs
            {
                Message = message,
                User = usr,
                Guild = guild,
                Emoji = emoji
            };
            await this._messageReactionAdded.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageReactionRemoveAsync(ulong userId, ulong messageId, ulong channelId, ulong? guildId, DiscordEmoji emoji)
        {
            var channel = this.InternalGetCachedChannel(channelId);

            emoji.Discord = this;

            if (!this.UserCache.TryGetValue(userId, out var user))
                user = new DiscordUser { Id = userId, Discord = this };

            if (channel?.Guild != null)
                user = channel.Guild.Members.TryGetValue(userId, out var member)
                    ? member
                    : new DiscordMember(user) { Discord = this, _guildId = channel.GuildId.Value };

            if (channel == null
                || this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var message))
            {
                message = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channelId,
                    Discord = this
                };
            }

            var reaction = message._reactions?.FirstOrDefault(react => react.Emoji == emoji);
            if (reaction != null)
            {
                reaction.Count--;
                reaction.IsMe &= this.CurrentUser.Id != userId;

                if (message._reactions != null && reaction.Count <= 0) // shit happens
                    for (var i = 0; i < message._reactions.Count; i++)
                        if (message._reactions[i].Emoji == emoji)
                        {
                            message._reactions.RemoveAt(i);
                            break;
                        }
            }

            var guild = this.InternalGetCachedGuild(guildId);

            var eventArgs = new MessageReactionRemoveEventArgs
            {
                Message = message,
                User = user,
                Guild = guild,
                Emoji = emoji
            };
            await this._messageReactionRemoved.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageReactionRemoveAllAsync(ulong messageId, ulong channelId, ulong? guildId)
        {
            var channel = this.InternalGetCachedChannel(channelId);

            if (channel == null
                || this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var message))
            {
                message = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channelId,
                    Discord = this
                };
            }

            message._reactions?.Clear();

            var guild = this.InternalGetCachedGuild(guildId);

            var eventArgs = new MessageReactionsClearEventArgs
            {
                Message = message,
                Guild = guild
            };

            await this._messageReactionsCleared.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnMessageReactionRemoveEmojiAsync(ulong messageId, ulong channelId, ulong guildId, JToken data)
        {
            var guild = this.InternalGetCachedGuild(guildId);
            var channel = this.InternalGetCachedChannel(channelId);

            if (channel == null
                || this.Configuration.MessageCacheSize == 0
                || this.MessageCache == null
                || !this.MessageCache.TryGet(cachedMessage => cachedMessage.Id == messageId && cachedMessage.ChannelId == channelId, out var message))
            {
                message = new DiscordMessage
                {
                    Id = messageId,
                    ChannelId = channelId,
                    Discord = this
                };
            }

            var partialEmoji = data.ToObject<DiscordEmoji>();

            if (!guild._emojis.TryGetValue(partialEmoji.Id, out var emoji))
            {
                emoji = partialEmoji;
                emoji.Discord = this;
            }

            message._reactions?.RemoveAll(r => r.Emoji.Equals(emoji));

            var eventArgs = new MessageReactionRemoveEmojiEventArgs
            {
                Channel = channel,
                Guild = guild,
                Message = message,
                Emoji = emoji
            };

            await this._messageReactionRemovedEmoji.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region User/Presence Update

        internal async Task OnPresenceUpdateEventAsync(JObject rawPresence, JObject rawUser)
        {
            var userId = (ulong)rawUser["id"];
            DiscordPresence oldPresence = null;

            if (this._presences.TryGetValue(userId, out var presence))
            {
                oldPresence = new DiscordPresence(presence);
                DiscordJson.PopulateObject(rawPresence, presence);
            }
            else
            {
                presence = rawPresence.ToObject<DiscordPresence>();
                presence.Discord = this;
                presence.Activity = new DiscordActivity(presence.RawActivity);
                this._presences[presence.InternalUser.Id] = presence;
            }

            // reuse arrays / avoid linq (this is a hot zone)
            if (presence.Activities == null || rawPresence["activities"] == null)
            {
                presence.InternalActivities = Array.Empty<DiscordActivity>();
            }
            else
            {
                if (presence.InternalActivities.Length != presence.RawActivities.Length)
                    presence.InternalActivities = new DiscordActivity[presence.RawActivities.Length];

                for (var i = 0; i < presence.InternalActivities.Length; i++)
                    presence.InternalActivities[i] = new DiscordActivity(presence.RawActivities[i]);

                if (presence.InternalActivities.Length > 0)
                {
                    presence.RawActivity = presence.RawActivities[0];

                    if (presence.Activity != null)
                        presence.Activity.UpdateWith(presence.RawActivity);
                    else
                        presence.Activity = new DiscordActivity(presence.RawActivity);
                }
            }

            if (this.UserCache.TryGetValue(userId, out var user))
            {
                if (oldPresence != null)
                {
                    oldPresence.InternalUser.Username = user.Username;
                    oldPresence.InternalUser.Discriminator = user.Discriminator;
                    oldPresence.InternalUser.AvatarHash = user.AvatarHash;
                }

                if (rawUser["username"] is object)
                    user.Username = (string)rawUser["username"];
                if (rawUser["discriminator"] is object)
                    user.Discriminator = (string)rawUser["discriminator"];
                if (rawUser["avatar"] is object)
                    user.AvatarHash = (string)rawUser["avatar"];

                presence.InternalUser.Username = user.Username;
                presence.InternalUser.Discriminator = user.Discriminator;
                presence.InternalUser.AvatarHash = user.AvatarHash;
            }

            var userAfter = user ?? new DiscordUser(presence.InternalUser);
            var eventArgs = new PresenceUpdateEventArgs
            {
                Status = presence.Status,
                Activity = presence.Activity,
                User = user,
                PresenceBefore = oldPresence,
                PresenceAfter = presence,
                UserBefore = oldPresence != null ? new DiscordUser(oldPresence.InternalUser) : userAfter,
                UserAfter = userAfter
            };
            await this._presenceUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnUserSettingsUpdateEventAsync(TransportUser user)
        {
            var newUser = new DiscordUser(user) { Discord = this };

            var eventArgs = new UserSettingsUpdateEventArgs
            {
                User = newUser
            };
            await this._userSettingsUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnUserUpdateEventAsync(TransportUser user)
        {
            var oldUser = new DiscordUser
            {
                AvatarHash = this.CurrentUser.AvatarHash,
                Discord = this,
                Discriminator = this.CurrentUser.Discriminator,
                Email = this.CurrentUser.Email,
                Id = this.CurrentUser.Id,
                IsBot = this.CurrentUser.IsBot,
                MfaEnabled = this.CurrentUser.MfaEnabled,
                Username = this.CurrentUser.Username,
                Verified = this.CurrentUser.Verified
            };

            this.CurrentUser.AvatarHash = user.AvatarHash;
            this.CurrentUser.Discriminator = user.Discriminator;
            this.CurrentUser.Email = user.Email;
            this.CurrentUser.Id = user.Id;
            this.CurrentUser.IsBot = user.IsBot;
            this.CurrentUser.MfaEnabled = user.MfaEnabled;
            this.CurrentUser.Username = user.Username;
            this.CurrentUser.Verified = user.Verified;

            var eventArgs = new UserUpdateEventArgs
            {
                UserAfter = this.CurrentUser,
                UserBefore = oldUser
            };
            await this._userUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Voice

        internal async Task OnVoiceStateUpdateEventAsync(JObject data)
        {
            var guildId = (ulong)data["guild_id"];
            var userId = (ulong)data["user_id"];
            var guild = this._guilds[guildId];

            var newVoiceState = data.ToObject<DiscordVoiceState>();
            newVoiceState.Discord = this;

            gld._voiceStates.TryRemove(uid, out var vstateOld);

            if (vstateNew.Channel != null)
            {
                gld._voiceStates[vstateNew.UserId] = vstateNew;
            }

            if (guild._members.TryGetValue(userId, out var member))
            {
                member.IsMuted = newVoiceState.IsServerMuted;
                member.IsDeafened = newVoiceState.IsServerDeafened;
            }

            var eventArgs = new VoiceStateUpdateEventArgs
            {
                Guild = newVoiceState.Guild,
                Channel = newVoiceState.Channel,
                User = newVoiceState.User,
                SessionId = newVoiceState.SessionId,

                Before = oldVoiceState,
                After = newVoiceState
            };
            await this._voiceStateUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnVoiceServerUpdateEventAsync(string endpoint, string token, DiscordGuild guild)
        {
            var eventArgs = new VoiceServerUpdateEventArgs
            {
                Endpoint = endpoint,
                VoiceToken = token,
                Guild = guild
            };
            await this._voiceServerUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Commands

        internal async Task OnApplicationCommandCreateAsync(DiscordApplicationCommand applicationCommand, ulong? guildId)
        {
            applicationCommand.Discord = this;

            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null && guildId.HasValue)
            {
                guild = new DiscordGuild
                {
                    Id = guildId.Value,
                    Discord = this
                };
            }

            var eventArgs = new ApplicationCommandEventArgs
            {
                Guild = guild,
                Command = applicationCommand
            };

            await this._applicationCommandCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnApplicationCommandUpdateAsync(DiscordApplicationCommand applicationCommand, ulong? guildId)
        {
            applicationCommand.Discord = this;

            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null && guildId.HasValue)
            {
                guild = new DiscordGuild
                {
                    Id = guildId.Value,
                    Discord = this
                };
            }

            var eventArgs = new ApplicationCommandEventArgs
            {
                Guild = guild,
                Command = applicationCommand
            };

            await this._applicationCommandUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnApplicationCommandDeleteAsync(DiscordApplicationCommand applicationCommand, ulong? guildId)
        {
            applicationCommand.Discord = this;

            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null && guildId.HasValue)
            {
                guild = new DiscordGuild
                {
                    Id = guildId.Value,
                    Discord = this
                };
            }

            var eventArgs = new ApplicationCommandEventArgs
            {
                Guild = guild,
                Command = applicationCommand
            };

            await this._applicationCommandDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Integration

        internal async Task OnIntegrationCreateAsync(DiscordIntegration integration, ulong guildId)
        {
            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null)
            {
                guild = new DiscordGuild
                {
                    Id = guildId,
                    Discord = this
                };
            }

            var eventArgs = new IntegrationCreateEventArgs
            {
                Guild = guild,
                Integration = integration
            };

            await this._integrationCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnIntegrationUpdateAsync(DiscordIntegration integration, ulong guildId)
        {
            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null)
            {
                guild = new DiscordGuild
                {
                    Id = guildId,
                    Discord = this
                };
            }

            var eventArgs = new IntegrationUpdateEventArgs
            {
                Guild = guild,
                Integration = integration
            };

            await this._integrationUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnIntegrationDeleteAsync(ulong integrationId, ulong guildId, ulong? applicationId)
        {
            var guild = this.InternalGetCachedGuild(guildId);

            if (guild == null)
            {
                guild = new DiscordGuild
                {
                    Id = guildId,
                    Discord = this
                };
            }

            var eventArgs = new IntegrationDeleteEventArgs
            {
                Guild = guild,
                Applicationid = applicationId,
                IntegrationId = integrationId
            };

            await this._integrationDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Stage Instance

        internal async Task OnStageInstanceCreateAsync(DiscordStageInstance instance)
        {
            instance.Discord = this;

            var guild = this.InternalGetCachedGuild(instance.GuildId);

            guild._stageInstances[instance.Id] = instance;

            var eventArgs = new StageInstanceCreateEventArgs
            {
                StageInstance = instance
            };

            await this._stageInstanceCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnStageInstanceUpdateAsync(DiscordStageInstance instance)
        {
            instance.Discord = this;

            var guild = this.InternalGetCachedGuild(instance.GuildId);

            if (!guild._stageInstances.TryRemove(instance.Id, out var oldInstance))
                oldInstance = new DiscordStageInstance { Id = instance.Id, GuildId = instance.GuildId, ChannelId = instance.ChannelId };

            guild._stageInstances[instance.Id] = instance;

            var eventArgs = new StageInstanceUpdateEventArgs
            {
                StageInstanceBefore = oldInstance,
                StageInstanceAfter = instance
            };

            await this._stageInstanceUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnStageInstanceDeleteAsync(DiscordStageInstance instance)
        {
            instance.Discord = this;

            var guild = this.InternalGetCachedGuild(instance.GuildId);

            guild._stageInstances.TryRemove(instance.Id, out _);

            var eventArgs = new StageInstanceDeleteEventArgs
            {
                StageInstance = instance
            };

            await this._stageInstanceDeleted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #region Misc

        internal async Task OnInteractionCreateAsync(ulong? guildId, ulong channelId, TransportUser transportUser, TransportMember transportMember, DiscordInteraction interaction)
        {
            var user = new DiscordUser(transportUser) { Discord = this };
            this.UpdateUserCache(user);

            if (transportMember != null)
                user = new DiscordMember(transportMember) { _guildId = guildId.Value, Discord = this };

            interaction.User = user;
            interaction.ChannelId = channelId;
            interaction.GuildId = guildId;
            interaction.Discord = this;
            interaction.Data.Discord = this;

            var resolved = interaction.Data.Resolved;
            if (resolved != null)
            {
                if (resolved.Users != null)
                {
                    foreach (var resolvedUser in resolved.Users)
                    {
                        resolvedUser.Value.Discord = this;
                        this.UpdateUserCache(resolvedUser.Value);
                    }
                }

                if (resolved.Members != null)
                {
                    foreach (var resolvedMember in resolved.Members)
                    {
                        resolvedMember.Value.Discord = this;
                        resolvedMember.Value.Id = resolvedMember.Key;
                        resolvedMember.Value._guildId = guildId.Value;
                        resolvedMember.Value.User.Discord = this;

                        this.UpdateUserCache(resolvedMember.Value.User);
                    }
                }

                if (resolved.Channels != null)
                {
                    foreach (var resolvedChannel in resolved.Channels)
                    {
                        resolvedChannel.Value.Discord = this;

                        if (guildId.HasValue)
                            resolvedChannel.Value.GuildId = guildId.Value;
                    }
                }

                if (resolved.Roles != null)
                {
                    foreach (var resolvedRole in resolved.Roles)
                    {
                        resolvedRole.Value.Discord = this;

                        if (guildId.HasValue)
                            resolvedRole.Value._guildId = guildId.Value;
                    }
                }

                if (resolved.Messages != null)
                {
                    foreach (var resolvedMessage in resolved.Messages)
                    {
                        resolvedMessage.Value.Discord = this;

                        if (guildId.HasValue)
                            resolvedMessage.Value.GuildId = guildId.Value;
                    }
                }
            }

            if (interaction.Type is InteractionType.Component)
            {
                interaction.Message.Discord = this;
                interaction.Message.ChannelId = interaction.ChannelId;
                var eventArgs = new ComponentInteractionCreateEventArgs
                {
                    Message = interaction.Message,
                    Interaction = interaction
                };

                await this._componentInteractionCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
            }
            else
            {
                if (interaction.Data.Target.HasValue) // Context-Menu. //
                {
                    var targetId = interaction.Data.Target.Value;
                    DiscordUser targetUser = null;
                    DiscordMember targetMember = null;
                    DiscordMessage targetMessage = null;

                    interaction.Data.Resolved.Messages?.TryGetValue(targetId, out targetMessage);
                    interaction.Data.Resolved.Members?.TryGetValue(targetId, out targetMember);
                    interaction.Data.Resolved.Users?.TryGetValue(targetId, out targetUser);

                    var eventArgs = new ContextMenuInteractionCreateEventArgs
                    {
                        Interaction = interaction,
                        TargetUser = targetMember ?? targetUser,
                        TargetMessage = targetMessage,
                        Type = interaction.Data.Type,
                    };
                    await this._contextMenuInteractionCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
                }
                else
                {
                    var eventArgs = new InteractionCreateEventArgs
                    {
                        Interaction = interaction
                    };

                    await this._interactionCreated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
                }
            }
        }

        internal async Task OnTypingStartEventAsync(ulong userId, ulong channelId, DiscordChannel channel, ulong? guildId, DateTimeOffset started, TransportMember transportMember)
        {
            if (channel == null)
            {
                channel = new DiscordChannel
                {
                    Discord = this,
                    Id = channelId,
                    GuildId = guildId ?? default,
                };
            }

            var guild = this.InternalGetCachedGuild(guildId);
            var user = this.UpdateUser(new DiscordUser { Id = userId, Discord = this }, guildId, guild, transportMember);

            var eventArgs = new TypingStartEventArgs
            {
                Channel = channel,
                User = user,
                Guild = guild,
                StartedAt = started
            };
            await this._typingStarted.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnWebhooksUpdateAsync(DiscordChannel channel, DiscordGuild guild)
        {
            var eventArgs = new WebhooksUpdateEventArgs
            {
                Channel = channel,
                Guild = guild
            };
            await this._webhooksUpdated.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        internal async Task OnStickersUpdatedAsync(IEnumerable<DiscordMessageSticker> newStickers, JObject data)
        {
            var guild = this.InternalGetCachedGuild((ulong)data["guild_id"]);
            var oldStickers = new ConcurrentDictionary<ulong, DiscordMessageSticker>(guild._stickers);

            guild._stickers.Clear();

            foreach (var sticker in newStickers)
            {
                if(sticker.User != null)
                    sticker.User.Discord = this;

                sticker.Discord = this;

                guild._stickers[sticker.Id] = sticker;
            }

            var eventArgs = new GuildStickersUpdateEventArgs
            {
                Guild = guild,
                StickersBefore = oldStickers,
                StickersAfter = guild.Stickers
            };

            await this._guildStickersUpdated.InvokeAsync(this, eventArgs);
        }

        internal async Task OnUnknownEventAsync(GatewayPayload payload)
        {
            var eventArgs = new UnknownEventArgs { EventName = payload.EventName, Json = (payload.Data as JObject)?.ToString() };
            await this._unknownEvent.InvokeAsync(this, eventArgs).ConfigureAwait(false);
        }

        #endregion

        #endregion
    }
}
