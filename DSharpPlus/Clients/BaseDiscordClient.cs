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

#pragma warning disable CS0618
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.Net;
using Microsoft.Extensions.Logging;

namespace DSharpPlus
{
    /// <summary>
    /// Represents a common base for various Discord client implementations.
    /// </summary>
    public abstract class BaseDiscordClient : IDisposable
    {
        internal protected DiscordApiClient ApiClient { get; }
        internal protected DiscordConfiguration Configuration { get; }

        /// <summary>
        /// Gets the instance of the logger for this client.
        /// </summary>
        public ILogger<BaseDiscordClient> Logger { get; }

        /// <summary>
        /// Gets the string representing the version of D#+.
        /// </summary>
        public string VersionString { get; }

        /// <summary>
        /// Gets the current user.
        /// </summary>
        public DiscordUser CurrentUser { get; internal set; }

        /// <summary>
        /// Gets the current application.
        /// </summary>
        public DiscordApplication CurrentApplication { get; internal set; }

        /// <summary>
        /// Gets the cached guilds for this client.
        /// </summary>
        public abstract IReadOnlyDictionary<ulong, DiscordGuild> Guilds { get; }

        /// <summary>
        /// Gets the cached users for this client.
        /// </summary>
        protected internal ConcurrentDictionary<ulong, DiscordUser> UserCache { get; }

        /// <summary>
        /// Gets the list of available voice regions. Note that this property will not contain VIP voice regions.
        /// </summary>
        public IReadOnlyDictionary<string, DiscordVoiceRegion> VoiceRegions
            => this._voiceRegionsLazy.Value;

        /// <summary>
        /// Gets the list of available voice regions. This property is meant as a way to modify <see cref="VoiceRegions"/>.
        /// </summary>
        protected internal ConcurrentDictionary<string, DiscordVoiceRegion> InternalVoiceRegions { get; set; }
        internal Lazy<IReadOnlyDictionary<string, DiscordVoiceRegion>> _voiceRegionsLazy;

        /// <summary>
        /// Initializes this Discord API client.
        /// </summary>
        /// <param name="config">Configuration for this client.</param>
        protected BaseDiscordClient(DiscordConfiguration config)
        {
            this.Configuration = new DiscordConfiguration(config);

            if (this.Configuration.LoggerFactory == null)
            {
                this.Configuration.LoggerFactory = new DefaultLoggerFactory();
                this.Configuration.LoggerFactory.AddProvider(new DefaultLoggerProvider(this));
            }
            this.Logger = this.Configuration.LoggerFactory.CreateLogger<BaseDiscordClient>();

            this.ApiClient = new DiscordApiClient(this);
            this.UserCache = new ConcurrentDictionary<ulong, DiscordUser>();
            this.InternalVoiceRegions = new ConcurrentDictionary<string, DiscordVoiceRegion>();
            this._voiceRegionsLazy = new Lazy<IReadOnlyDictionary<string, DiscordVoiceRegion>>(() => new ReadOnlyDictionary<string, DiscordVoiceRegion>(this.InternalVoiceRegions));

            var clientAssembly = typeof(DiscordClient).GetTypeInfo().Assembly;

            var clientCustomAttribute = clientAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (clientCustomAttribute != null)
            {
                this.VersionString = clientCustomAttribute.InformationalVersion;
            }
            else
            {
                var clientVersion = clientAssembly.GetName().Version;
                var threeNumberedVersion = clientVersion.ToString(3);

                if (clientVersion.Revision > 0)
                    this.VersionString = $"{threeNumberedVersion}, CI build {clientVersion.Revision}";
            }
        }

        /// <summary>
        /// Gets the current API application.
        /// </summary>
        /// <returns>Current API application.</returns>
        public async Task<DiscordApplication> GetCurrentApplicationAsync()
        {
            var transportApplication = await this.ApiClient.GetCurrentApplicationInfoAsync().ConfigureAwait(false);
            var application = new DiscordApplication
            {
                Discord = this,
                Id = transportApplication.Id,
                Name = transportApplication.Name,
                Description = transportApplication.Description,
                Summary = transportApplication.Summary,
                IconHash = transportApplication.IconHash,
                TermsOfServiceUrl = transportApplication.TermsOfServiceUrl,
                PrivacyPolicyUrl = transportApplication.PrivacyPolicyUrl,
                RpcOrigins = transportApplication.RpcOrigins != null ? new ReadOnlyCollection<string>(transportApplication.RpcOrigins) : null,
                Flags = transportApplication.Flags,
                RequiresCodeGrant = transportApplication.BotRequiresCodeGrant,
                IsPublic = transportApplication.IsPublicBot,
                CoverImageHash = null
            };

            // do team and owners
            // tbh fuck doing this properly
            if (transportApplication.Team == null)
            {
                // singular owner

                application.Owners = new ReadOnlyCollection<DiscordUser>(new[] { new DiscordUser(transportApplication.Owner) });
                application.Team = null;
            }
            else
            {
                // team owner

                application.Team = new DiscordTeam(transportApplication.Team);

                var members = transportApplication.Team.Members
                    .Select(member => new DiscordTeamMember(member) { Team = application.Team, User = new DiscordUser(member.User) })
                    .ToArray();

                var owners = members
                    .Where(member => member.MembershipStatus == DiscordTeamMembershipStatus.Accepted)
                    .Select(member => member.User)
                    .ToArray();

                application.Owners = new ReadOnlyCollection<DiscordUser>(owners);
                application.Team.Owner = owners.FirstOrDefault(owner => owner.Id == transportApplication.Team.OwnerId);
                application.Team.Members = new ReadOnlyCollection<DiscordTeamMember>(members);
            }

            return application;
        }

        /// <summary>
        /// Gets a list of regions
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<IReadOnlyList<DiscordVoiceRegion>> ListVoiceRegionsAsync()
            => this.ApiClient.ListVoiceRegionsAsync();

        /// <summary>
        /// Initializes this client. This method fetches information about current user, application, and voice regions.
        /// </summary>
        /// <returns></returns>
        public virtual async Task InitializeAsync()
        {
            if (this.CurrentUser == null)
            {
                this.CurrentUser = await this.ApiClient.GetCurrentUserAsync().ConfigureAwait(false);
                this.UpdateUserCache(this.CurrentUser);
            }

            if (this.Configuration.TokenType == TokenType.Bot && this.CurrentApplication == null)
                this.CurrentApplication = await this.GetCurrentApplicationAsync().ConfigureAwait(false);

            if (this.Configuration.TokenType != TokenType.Bearer && this.InternalVoiceRegions.Count == 0)
            {
                var voiceRegions = await this.ListVoiceRegionsAsync().ConfigureAwait(false);
                foreach (var voiceRegion in voiceRegions)
                    this.InternalVoiceRegions.TryAdd(voiceRegion.Id, voiceRegion);
            }
        }

        /// <summary>
        /// Gets the current gateway info for the provided token.
        /// <para>If no value is provided, the configuration value will be used instead.</para>
        /// </summary>
        /// <returns>A gateway info object.</returns>
        public async Task<GatewayInfo> GetGatewayInfoAsync(string token = null)
        {
            if (this.Configuration.TokenType != TokenType.Bot)
                throw new InvalidOperationException("Only bot tokens can access this info.");

            if (string.IsNullOrEmpty(this.Configuration.Token))
            {
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("Could not locate a valid token.");

                this.Configuration.Token = token;

                var getawayInfo = await this.ApiClient.GetGatewayInfoAsync().ConfigureAwait(false);
                this.Configuration.Token = null;
                return getawayInfo;
            }

            return await this.ApiClient.GetGatewayInfoAsync().ConfigureAwait(false);
        }

        internal DiscordUser GetCachedOrEmptyUserInternal(ulong userId)
        {
            this.TryGetCachedUserInternal(userId, out var user);
            return user;
        }

        internal bool TryGetCachedUserInternal(ulong userId, out DiscordUser user)
        {
            if (this.UserCache.TryGetValue(userId, out user))
                return true;

            user = new DiscordUser { Id = userId, Discord = this };
            return false;
        }

        internal DiscordUser UpdateUserCache(DiscordUser newUser)
        {
            return this.UserCache.AddOrUpdate(newUser.Id, newUser, (id, oldUser) =>
            {
                oldUser.Username = newUser.Username;
                oldUser.Discriminator = newUser.Discriminator;
                oldUser.AvatarHash = newUser.AvatarHash;
                return oldUser;
            });
        }

        /// <summary>
        /// Disposes this client.
        /// </summary>
        public abstract void Dispose();
    }
}
