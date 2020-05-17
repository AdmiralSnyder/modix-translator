﻿using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ModixTranslator.Behaviors;
using ModixTranslator.Models.Translator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModixTranslator.HostedServices
{
    public class TranslatorHostedService : ITranslatorHostedService
    {
        private readonly ILogger<TranslatorHostedService> _logger;
        private readonly ITranslationService _translation;
        private readonly IBotService _bot;
        private readonly ConcurrentDictionary<string, ChannelPair> _channelPairs = new ConcurrentDictionary<string, ChannelPair>();

        public TranslatorHostedService(ILogger<TranslatorHostedService> logger, ITranslationService translationService, IBotService bot)
        {
            _logger = logger;
            _translation = translationService;
            _bot = bot;
        }

        public async Task<ChannelPair?> GetOrCreateChannelPair(SocketGuild guild, string lang)
        {
            string safeLang = GetSafeLangString(lang);
            if (_channelPairs.TryGetValue(safeLang, out var pair))
            {
                return pair;
            }

            var category = guild.CategoryChannels.SingleOrDefault(a => a.Name == TranslationConstants.CategoryName);
            if (category == default)
            {
                throw new InvalidOperationException($"The channel category {TranslationConstants.CategoryName} does not exist");
            }

            var supportedLang = await _translation.IsLangSupported(lang);

            if (!supportedLang)
            {
                throw new LanguageNotSupportedException($"{lang} is not supported at this time.");
            }

            var fromLangName = $"from-{safeLang}-to-{TranslationConstants.StandardLanguage}";
            var toLangName = $"to-{safeLang}-from-{TranslationConstants.StandardLanguage}";
            var fromLangTopic = await _translation.GetTranslation(TranslationConstants.StandardLanguage, lang, $"Responses will be translated to {TranslationConstants.StandardLanguage} and posted in this channel's pair `#from-{TranslationConstants.StandardLanguage}-to-{lang}`");
            var toLangTopic = $"Responses will be translated to {lang} and posted in this channel's pair `#from-{lang}-to-{TranslationConstants.StandardLanguage}`";

            var fromLangChannel = await guild.CreateTextChannelAsync(fromLangName, p =>
            {
                p.CategoryId = category.Id;
                p.Topic = fromLangTopic;
            });

            var ToLangChannel = await guild.CreateTextChannelAsync(toLangName, p =>
            {
                p.CategoryId = category.Id;
                p.Topic = toLangTopic;
            });
            pair = new ChannelPair
            {
                TranslationChannel = fromLangChannel,
                StandardLangChanel = ToLangChannel
            };

            if (!_channelPairs.TryAdd(safeLang, pair))
            {
                _logger.LogWarning($"The channel pairs {{{fromLangName}, {toLangName}}} have already been tracked, cleaning up");
                await pair.TranslationChannel.DeleteAsync();
                await pair.StandardLangChanel.DeleteAsync();
                _channelPairs.TryGetValue(safeLang, out pair);
            }

            return pair;
        }

        private static string GetSafeLangString(string lang)
        {
            return lang.ToLower().Replace("-", "_");
        }

        private Task MessageUpdated(Cacheable<IMessage, ulong> lastMessage, SocketMessage newMessage, ISocketMessageChannel channel)
        {
            if (!(newMessage.Channel is SocketTextChannel messageChannel))
            {
                return Task.CompletedTask;
            }

            if (messageChannel.Category == null)
            {
                return Task.CompletedTask;
            }

            if (messageChannel.Category.Name != TranslationConstants.CategoryName)
            {
                return Task.CompletedTask;
            }

            //todo: message editing
            return Task.CompletedTask;
        }

        private Task MessageReceived(SocketMessage message)
        {
            if (message.Author.Id == _bot.DiscordClient.CurrentUser.Id)
            {
                return Task.CompletedTask;
            }

            if (!(message.Author is SocketGuildUser guildUser))
            {
                return Task.CompletedTask;
            }

            if (!(message.Channel is SocketTextChannel messageChannel))
            {
                return Task.CompletedTask;
            }

            if (!(messageChannel.Category is SocketCategoryChannel categoryChannel))
            {
                return Task.CompletedTask;
            }

            if (messageChannel.Category.Name != TranslationConstants.CategoryName)
            {
                return Task.CompletedTask;
            }

            if (TranslationConstants.PermanentChannels.Contains(messageChannel.Name))
            {
                return Task.CompletedTask;
            }

            var lang = messageChannel.GetLangFromChannelName();

            if (lang == null)
            {
                return Task.CompletedTask;
            }

            var safeLang = GetSafeLangString(lang);

            if (!_channelPairs.TryGetValue(safeLang, out var pair))
            {
                _logger.LogWarning("Message received from a loc channel without a valid pair");
                return Task.CompletedTask;
            }

            _logger.LogDebug("Starting translation of message");

            _bot.ExecuteHandlerAsyncronously<(SocketCategoryChannel category, string original, string translated)>(
                handler: async discord =>
                {
                    if(pair?.TranslationChannel == null || pair?.StandardLangChanel == null)
                    {
                        throw new InvalidOperationException("Invalid channel pair");
                    }

                    string relayText = string.Empty;
                    if (messageChannel.Id == pair.StandardLangChanel.Id)
                    {
                        relayText = await SendMessageToPartner(message, $"{guildUser.Nickname ?? guildUser.Username}", pair.TranslationChannel, TranslationConstants.StandardLanguage, lang);
                    }
                    else if (messageChannel.Id == pair.TranslationChannel.Id)
                    {
                        relayText = await SendMessageToPartner(message, $"{guildUser.Nickname ?? guildUser.Username}", pair.StandardLangChanel, lang, TranslationConstants.StandardLanguage);
                    }

                    return (categoryChannel, message.Content, relayText);
                },
                callback: async result =>
                {
                    if (result.category == null || string.IsNullOrWhiteSpace(result.original) || string.IsNullOrWhiteSpace(result.translated))
                    {
                        return;
                    }

                    var historyChannel = result.category.Channels.OfType<SocketTextChannel>()
                        .SingleOrDefault(a => a.Name == TranslationConstants.HistoryChannelName);
                    if (historyChannel == null)
                    {
                        return;
                    }

                    _logger.LogDebug("Sending messages to the history channel");

                    // todo: make into an embed
                    await historyChannel.SendMessageAsync($"{guildUser.Nickname ?? guildUser.Username}: {result.original}");
                    await historyChannel.SendMessageAsync($"{guildUser.Nickname ?? guildUser.Username}: {result.translated}");

                    _logger.LogDebug("Completed translating messages");
                });

            return Task.CompletedTask;
        }

        private async Task<string> SendMessageToPartner(SocketMessage message, string username, ITextChannel targetChannel, string from, string to)
        {
            var isStandardLang = targetChannel.IsStandardLangChannel();

            _logger.LogDebug($"Message received from {from} channel '{message.Channel.Name}', sending to {targetChannel.Name}");
            string relayText = string.Empty;
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                relayText = await _translation.GetTranslation(from, to, message.Content);
            }

            if (message.Attachments.Count != 0)
            {
                relayText += $" {string.Join(" ", message.Attachments.Select(a => a.Url))}";
            }


            await targetChannel.SendMessageAsync($"{username}: {relayText}");
            return relayText;
        }

        private Task RemoveChannelFromMap(SocketChannel channel)
        {
            string foundLang = string.Empty;
            foreach (var pair in _channelPairs)
            {
                if(pair.Value?.TranslationChannel == null || pair.Value?.StandardLangChanel == null)
                {
                    _logger.LogWarning("invalid channel pair detected");
                    continue;
                }

                if (channel.Id == pair.Value.StandardLangChanel.Id || channel.Id == pair.Value.TranslationChannel.Id)
                {
                    foundLang = pair.Key;
                }
            }

            if (foundLang != string.Empty)
            {
                _logger.LogDebug($"One of the channels in a pair were deleted, removing pair '{foundLang}' from map");
                _channelPairs.TryRemove(foundLang, out _);
            }

            return Task.CompletedTask;
        }

        private Task BuildChannelMap(SocketGuild guild)
        {
            var category = guild.CategoryChannels.SingleOrDefault(a => a.Name == TranslationConstants.CategoryName);
            if (category == null)
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug($"Guild available for {guild.Name}, rebuilding pair map");
            var tempChannels = category.Channels.OfType<ITextChannel>().Where(a => !TranslationConstants.PermanentChannels.Contains(a.Name)).ToList();

            if (tempChannels.Count == 0)
            {
                return Task.CompletedTask;
            }

            var pairs = new Dictionary<string, ChannelPair>();

            foreach (var channel in tempChannels)
            {
                _logger.LogDebug($"Checking {channel.Name}");
                var lang = channel.GetLangFromChannelName();
                if (lang == null)
                {
                    _logger.LogDebug($"{channel.Name} is not a translation channel, skipping");
                    continue;
                }

                var safeLang = GetSafeLangString(lang);
                var isStandardLangChannel = channel.IsStandardLangChannel();
                _logger.LogDebug($"channel is the {TranslationConstants.StandardLanguage} lang channel? {isStandardLangChannel}");

                if (!pairs.TryGetValue(safeLang, out var pair))
                {
                    _logger.LogDebug("Creating new pair");
                    pair = new ChannelPair();
                    pairs[safeLang] = pair;
                }

                if (isStandardLangChannel)
                {
                    pair.StandardLangChanel = channel;
                }
                else
                {
                    pair.TranslationChannel = channel;
                }
            }

            foreach (var pair in pairs.ToList())
            {
                if (pair.Value.StandardLangChanel == default || pair.Value.TranslationChannel == default)
                {
                    _logger.LogDebug($"Pair is missing either the language channel or the {TranslationConstants.StandardLanguage} channel, skipping");
                    continue;
                }

                _logger.LogDebug($"Addping pair for {pair.Key}");
                _channelPairs[pair.Key] = pair.Value;
            }
            _logger.LogDebug($"Completed rebuilding pair map");
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Localizer starting up");
            _bot.DiscordClient.MessageReceived += MessageReceived;
            _bot.DiscordClient.MessageUpdated += MessageUpdated;
            _bot.DiscordClient.ChannelDestroyed += RemoveChannelFromMap;

            if (_bot.DiscordClient.ConnectionState == ConnectionState.Connected && _bot.DiscordClient.Guilds.Count > 0)
            {
                _logger.LogDebug("Discord bot is already connected, rebuilding pair map");
                foreach (var guild in _bot.DiscordClient.Guilds)
                {
                    await BuildChannelMap(guild);
                }
            }
            else
            {
                _logger.LogDebug("Discord bot has not connected, registering the GuildAvailable event");
                _bot.DiscordClient.GuildAvailable += BuildChannelMap;
            }

            _logger.LogDebug("Localizer started");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _bot.DiscordClient.MessageReceived -= MessageReceived;
            _bot.DiscordClient.MessageUpdated -= MessageUpdated;
            _bot.DiscordClient.ChannelDestroyed -= RemoveChannelFromMap;
            _bot.DiscordClient.GuildAvailable -= BuildChannelMap;
            return Task.CompletedTask;
        }
    }
}
