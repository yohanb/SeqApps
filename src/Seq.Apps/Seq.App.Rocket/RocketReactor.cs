﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Seq.Apps;
using Seq.Apps.LogEvents;
using SeqApps.Commons;

namespace Seq.App.Rocket
{
    [SeqApp("Rocket.Chat",
    Description = "Posts seq event as a message to a Rocket.Chat channel")]
    public class RocketReactor : Reactor, ISubscribeTo<LogEventData>
    {

        #region Settings 

        [SeqAppSetting(
            DisplayName = "Seq Server Url",
            IsOptional = true,
            HelpText = "URL of the seq server. This appears as a lin in message title")]
        public string SeqUrl { get; set; }

        [SeqAppSetting(DisplayName = "Rocket REST API Url",
            IsOptional = false,
             HelpText = "REST API Url of your Rocket.Chat server")]
        public string RocketApiUrl{ get; set; }

        [SeqAppSetting(DisplayName = "Rocket.Chat Channel",
            IsOptional = false,
             HelpText = "Name of Rocket.Chat channel")]
        public string Channel { get; set; }


        [SeqAppSetting(DisplayName = "Comma seperates list of event levels",
            IsOptional = true,
            HelpText = "If specified Jira issue will be created only for the specified event levels, other levels will be discarded")]
        public string LogEventLevels { get; set; }

        public List<LogEventLevel> LogEventLevelList
        {
            get
            {
                List<LogEventLevel> result = new List<LogEventLevel>();
                if (!(LogEventLevels?.HasValue() ?? false))
                    return result;

                var strValues = LogEventLevels.Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if ((strValues?.Length ?? 0) == 0)
                    return result;

                strValues.Aggregate(result, (acc, strValue) =>
                {
                    LogEventLevel enumValue = LogEventLevel.Debug;
                    if (Enum.TryParse<LogEventLevel>(strValue, out enumValue))
                        acc.Add(enumValue);
                    return acc;
                });

                return result;
            }
        }

        [SeqAppSetting(
        DisplayName = "Attach Properties",
        IsOptional = true,
        InputType = SettingInputType.Checkbox,
        HelpText =
            "Attach event data structured properties to the message"
        )]
        public bool AttachProperties{ get; set; } = false;

        [SeqAppSetting(
            DisplayName = "Rocket Username",
            IsOptional = false)]
        public string Username { get; set; }

        [SeqAppSetting(
            DisplayName = "Rocket Password",
            IsOptional = false,
            InputType = SettingInputType.Password)]
        public string Password { get; set; }


        private string _step;
        #endregion //Settings

        public void On(Event<LogEventData> evt)
        {
            try
            {
                var result = PostMessage(evt).Result;

            }
            catch (AggregateException aex)
            {
                var fex = aex.Flatten();
                Log.Error(fex, "Error while sending message to Rocket.Chat : {_step}", _step);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while sending message to Rocket.Chat: {_step}", _step);
            }
        }

        private async Task<bool> PostMessage(Event<LogEventData> evt)
        {

            if (evt == null)
                return false;

            if ((LogEventLevelList?.Count ?? 0) > 0 && !LogEventLevelList.Contains(evt.Data.Level))
                return false;

            if (!(Channel ?? "").HasValue())
                return false;

            var apiBaseUrl = RocketApiUrl.NormalizeHostOrFQDN();
            var client = new JsonRestClient(apiBaseUrl);
            client.DoNotAuthorize = true;

            // Get the auth ticket
            _step = "Will authenticate: " + client.GetUriForResource("login");
            var authTicket =
                await
                    client.PostAsync<RocketAuthTicket,RocketAuthPayload>("login", new RocketAuthPayload { username = Username, password = Password})
                        .ConfigureAwait(false);

            if (authTicket?.data == null || authTicket.status != "success")
            {
                var e = new ApplicationException("Can not authenticate Rocket.Chat with the specified username/passwod");
                Log.Error(e,"Rocket.Chat authentication failure");
                return false;
            }

            _step = "Done authenticate";

            var renderedMessage = (evt?.Data?.RenderedMessage ?? "");
            var summary = renderedMessage.CleanCRLF().TruncateWithEllipsis(255);


            var channel = Channel.StartsWith("#") ? Channel.Trim() : $"#{Channel.Trim()}";
            var seqUrl = SeqUrl.NormalizeHostOrFQDN();
            var eventUrl = $"{seqUrl}#/events?filter=@Id%20%3D%20'{evt.Id}'";

            // Create the rocket message
            var rocketMessage = new RocketChatMessage
            {
                channel = channel,
                text = $"[{summary}]({eventUrl})"
            };

            // Attach the rendered message
            if (renderedMessage.HasValue())
            {
                var attachment = new RocketChatMessageAttachment
                {
                    color = RocketChatMessageAttachment.ColorByLevel(evt.Data.Level),
                    title = summary,
                    text = renderedMessage,
                    title_link = eventUrl
                };

                rocketMessage.attachments = rocketMessage.attachments ?? new List<RocketChatMessageAttachment>();
                rocketMessage.attachments.Add(attachment);
            }

            // If event has exception attach that Exception
            if ((evt?.Data?.Exception ?? "").HasValue())
            {
                var attachment = new RocketChatMessageAttachment
                {
                    color = RocketChatMessageAttachment.ColorByLevel(evt.Data.Level),
                    title = evt.Data.Exception,
                    text = evt.Data.Level.ToString(),
                    title_link = eventUrl
                };

                rocketMessage.attachments = rocketMessage.attachments ?? new List<RocketChatMessageAttachment>();
                rocketMessage.attachments.Add(attachment);
            }


            // Attach structured properties
            if (AttachProperties && (evt?.Data?.Properties?.Count ?? 0) > 0 )
            {
                var attachment = new RocketChatMessageAttachment
                {
                    color = RocketChatMessageAttachment.ColorByLevel(evt.Data.Level),
                    title = "Structured Event Properties",
                    title_link = eventUrl,
                    collapsed = true
                };

                var allProps = evt.Data.Properties;
                foreach (var kvp in allProps)
                {
                    var field = new RocketChatMessageAttachmentField
                    {
                        @short = false,
                        title = kvp.Key,
                        value = kvp.Value != null ? JsonConvert.SerializeObject(kvp.Value) : ""
                    };
                    attachment.fields = attachment.fields ?? new List<RocketChatMessageAttachmentField>();
                    attachment.fields.Add(field);
                }

                rocketMessage.attachments = rocketMessage.attachments ?? new List<RocketChatMessageAttachment>();
                rocketMessage.attachments.Add(attachment);
            }

            // Add auth token data to request header
            Dictionary<string, string> authHeaders = new Dictionary<string, string>
            {
                ["X-User-Id"]=authTicket.data.userId,
                ["X-Auth-Token"]=authTicket.data.authToken,
            };

            _step = "Will post message";

            // Post the message
            var postMessageResult = await client.PostAsync<RocketChatPostMessageResult, RocketChatMessage>("chat.postMessage", rocketMessage,authHeaders).ConfigureAwait(false);

            if (postMessageResult == null || !postMessageResult.success)
            {
                var e = new ApplicationException("Can not post message to Rocket.Chat");
                var error = (postMessageResult?.error??"");
                Log.Error(e, "Rocket.Chat post message failure : {error}", error);
                return false;
            }

            _step = "Done post message";
            return true;
        }
    }
}