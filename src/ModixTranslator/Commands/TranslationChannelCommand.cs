﻿using Discord.Commands;
using ModixTranslator.HostedServices;
using ModixTranslator.Models.Translator;
using System.Threading.Tasks;

namespace ModixTranslator.Commands
{
    [Group("translate")]
    public class TranslationChannelCommand : ModuleBase<SocketCommandContext>
    {
        private readonly ITranslatorHostedService _localizer;

        public TranslationChannelCommand(ITranslatorHostedService localizer)
        {
            _localizer = localizer;
        }

        [Command("create")]
        public async Task Create(string lang)
        {
            try
            {
                var pair = await _localizer.GetOrCreateChannelPair(Context.Guild, lang);

                if(pair?.EnglishChannel == null || pair?.LangChannel == null)
                {
                    await Context.Channel.SendMessageAsync("Unable to create channel pair");
                    return;
                }

                await Context.Channel.SendMessageAsync($"Translation channels have been created at {pair.EnglishChannel.Mention} and {pair.LangChannel.Mention}");
            }
            catch(LanguageNotSupportedException ex)
            {
                await Context.Channel.SendMessageAsync(ex.Message);
            }
        }
    }
}
