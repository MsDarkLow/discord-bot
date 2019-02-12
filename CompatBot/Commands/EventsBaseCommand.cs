﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CompatBot.Database;
using CompatBot.Database.Providers;
using CompatBot.Utils;
using DSharpPlus.CommandsNext;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity;
using Microsoft.EntityFrameworkCore;

namespace CompatBot.Commands
{
    internal class EventsBaseCommand: BaseCommandModuleCustom
    {
        private static readonly Regex Duration = new Regex(@"((?<days>\d+)(\.|d\s*))?((?<hours>\d+)(\:|h\s*))?((?<mins>\d+)m?)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

        protected async Task NearestEvent(CommandContext ctx, string eventName = null)
        {
            var current = DateTime.UtcNow;
            var currentTicks = current.Ticks;
            using (var db = new BotDb())
            {
                var currentEvent = await db.EventSchedule.OrderBy(e => e.End).FirstOrDefaultAsync(e => e.Start <= currentTicks && e.End >= currentTicks).ConfigureAwait(false);
                var nextEvent = await db.EventSchedule.OrderBy(e => e.Start).FirstOrDefaultAsync(e => e.Start > currentTicks).ConfigureAwait(false);
                if (string.IsNullOrEmpty(eventName))
                {
                    var nearestEventMsg = "";
                    if (currentEvent != null)
                        nearestEventMsg = $"Current event: {currentEvent.Name} (going for {FormatCountdown(current - currentEvent.Start.AsUtc())})\n";
                    if (nextEvent != null)
                        nearestEventMsg += $"Next event: {nextEvent.Name} (starts in {FormatCountdown(nextEvent.Start.AsUtc() - current)})";
                    await ctx.RespondAsync(nearestEventMsg.TrimEnd()).ConfigureAwait(false);
                    return;
                }

                eventName = await FuzzyMatchEventName(db, eventName).ConfigureAwait(false);
                var promo = "";
                if (currentEvent != null)
                    promo = $"\nMeanwhile check out this {(string.IsNullOrEmpty(currentEvent.EventName) ? "" : currentEvent.EventName + " " + currentEvent.Year + " ")}event in progress: {currentEvent.Name} (going for {FormatCountdown(current - currentEvent.Start.AsUtc())})";
                else if (nextEvent != null)
                    promo = $"\nMeanwhile check out this upcoming {(string.IsNullOrEmpty(nextEvent.EventName) ? "" : nextEvent.EventName + " " + nextEvent.Year + " ")}event: {nextEvent.Name} (starts in {FormatCountdown(nextEvent.Start.AsUtc() - current)})";
                var firstNamedEvent = await db.EventSchedule.OrderBy(e => e.Start).FirstOrDefaultAsync(e => e.Year >= current.Year && e.EventName == eventName).ConfigureAwait(false);
                if (firstNamedEvent == null)
                {
                    var noEventMsg = $"No information about the upcoming {eventName} at the moment";
                    if (!string.IsNullOrEmpty(promo))
                        noEventMsg += promo;
                    await ctx.RespondAsync(noEventMsg).ConfigureAwait(false);
                    return;
                }

                if (firstNamedEvent.Start >= currentTicks)
                {
                    var upcomingNamedEventMsg = $"__{FormatCountdown(firstNamedEvent.Start.AsUtc() - current)} until {eventName} {current.Year}!__";
                    if (string.IsNullOrEmpty(promo))
                        upcomingNamedEventMsg += $"\nFirst event: {firstNamedEvent.Name}";
                    else
                        upcomingNamedEventMsg += promo;
                    await ctx.RespondAsync(upcomingNamedEventMsg).ConfigureAwait(false);
                    return;
                }

                var lastNamedEvent = await db.EventSchedule.OrderByDescending(e => e.End).FirstOrDefaultAsync(e => e.Year == current.Year && e.EventName == eventName).ConfigureAwait(false);
                if (lastNamedEvent.End <= currentTicks)
                {
                    var e3EndedMsg = $"__{eventName} {current.Year} has ended. See you next year!__";
                    if (!string.IsNullOrEmpty(promo))
                        e3EndedMsg += promo;
                    await ctx.RespondAsync(e3EndedMsg).ConfigureAwait(false);
                    return;
                }

                var currentNamedEvent = await db.EventSchedule.OrderBy(e => e.End).FirstOrDefaultAsync(e => e.Start <= currentTicks && e.End >= currentTicks && e.EventName == eventName).ConfigureAwait(false);
                var nextNamedEvent = await db.EventSchedule.OrderBy(e => e.Start).FirstOrDefaultAsync(e => e.Start > currentTicks && e.EventName == eventName).ConfigureAwait(false);
                var msg = $"__{eventName} {current.Year} is already in progress!__\n";
                if (currentNamedEvent != null)
                    msg += $"Current event: {currentNamedEvent.Name} (going for {FormatCountdown(current - currentNamedEvent.Start.AsUtc())})\n";
                if (nextNamedEvent != null)
                    msg += $"Next event: {nextNamedEvent.Name} (starts in {FormatCountdown(nextNamedEvent.Start.AsUtc() - current)})";
                await ctx.SendAutosplitMessageAsync(msg.TrimEnd(), blockStart: "", blockEnd: "").ConfigureAwait(false);
            }
        }

        protected async Task Add(CommandContext ctx, string eventName = null)
        {
            var evt = new EventSchedule();
            var (success, msg) = await EditEventPropertiesAsync(ctx, evt, eventName).ConfigureAwait(false);
            if (success)
            {
                using (var db = new BotDb())
                {
                    await db.EventSchedule.AddAsync(evt).ConfigureAwait(false);
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
                await ctx.ReactWithAsync(Config.Reactions.Success).ConfigureAwait(false);
                await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Added a new schedule entry").ConfigureAwait(false);
            }
            else
                await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Event creation aborted").ConfigureAwait(false);
        }

        protected async Task Remove(CommandContext ctx, params int[] ids)
        {
            int removedCount;
            using (var db = new BotDb())
            {
                var eventsToRemove = await db.EventSchedule.Where(e3e => ids.Contains(e3e.Id)).ToListAsync().ConfigureAwait(false);
                db.EventSchedule.RemoveRange(eventsToRemove);
                removedCount = await db.SaveChangesAsync().ConfigureAwait(false);
            }
            if (removedCount == ids.Length)
                await ctx.RespondAsync($"Event{StringUtils.GetSuffix(ids.Length)} successfully removed!").ConfigureAwait(false);
            else
                await ctx.RespondAsync($"Removed {removedCount} event{StringUtils.GetSuffix(removedCount)}, but was asked to remove {ids.Length}").ConfigureAwait(false);
        }

        protected async Task Clear(CommandContext ctx, int? year = null)
        {
            var currentYear = DateTime.UtcNow.Year;
            int removedCount;
            using (var db = new BotDb())
            {
                var itemsToRemove = await db.EventSchedule.Where(e =>
                    year.HasValue
                        ? e.Year == year
                        : e.Year < currentYear
                ).ToListAsync().ConfigureAwait(false);
                db.EventSchedule.RemoveRange(itemsToRemove);
                removedCount = await db.SaveChangesAsync().ConfigureAwait(false);
            }
            await ctx.RespondAsync($"Removed {removedCount} event{(removedCount == 1 ? "" : "s")}").ConfigureAwait(false);
        }

        protected async Task Update(CommandContext ctx, int id, string eventName = null)
        {
            using (var db = new BotDb())
            {
                var evt = eventName == null
                    ? db.EventSchedule.FirstOrDefault(e => e.Id == id)
                    : db.EventSchedule.FirstOrDefault(e => e.Id == id && e.EventName == eventName);
                if (evt == null)
                {
                    await ctx.ReactWithAsync(Config.Reactions.Failure, $"No event with id {id}").ConfigureAwait(false);
                    return;
                }

                var (success, msg) = await EditEventPropertiesAsync(ctx, evt, eventName).ConfigureAwait(false);
                if (success)
                {
                    await db.SaveChangesAsync().ConfigureAwait(false);
                    await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Updated the schedule entry").ConfigureAwait(false);
                }
                else
                {
                    await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Event update aborted, changes weren't saved").ConfigureAwait(false);
                }
            }
        }

        protected async Task List(CommandContext ctx, string eventName = null, int? year = null)
        {
            var showAll = "all".Equals(eventName, StringComparison.InvariantCultureIgnoreCase);
            var currentTicks = DateTime.UtcNow.Ticks;
            List<EventSchedule> events;
            using (var db = new BotDb())
            {
                IQueryable<EventSchedule> query = db.EventSchedule;
                if (year.HasValue)
                    query = query.Where(e => e.Year == year);
                else
                {
                    if (!ctx.Channel.IsPrivate && !showAll)
                        query = query.Where(e => e.End > currentTicks);
                }
                if (!string.IsNullOrEmpty(eventName) && !showAll)
                {
                    eventName = await FuzzyMatchEventName(db, eventName).ConfigureAwait(false);
                    query = query.Where(e => e.EventName == eventName);
                }
                events = await query
                    .OrderBy(e => e.Start)
                    .ToListAsync()
                    .ConfigureAwait(false);
            }
            if (events.Count == 0)
            {
                await ctx.RespondAsync("There are no events to show").ConfigureAwait(false);
                return;
            }

            var msg = new StringBuilder();
            var currentYear = -1;
            var currentEvent = Guid.NewGuid().ToString();
            foreach (var evt in events)
            {
                if (evt.Year != currentYear)
                {
                    if (currentYear > 0)
                        msg.AppendLine();
                    currentEvent = Guid.NewGuid().ToString();
                    currentYear = evt.Year;
                }

                var evtName = evt.EventName ?? "";
                if (currentEvent != evtName)
                {
                    currentEvent = evtName;
                    var printName = string.IsNullOrEmpty(currentEvent) ? "Various independent events" : $"**{currentEvent} {currentYear} schedule**";
                    msg.AppendLine($"{printName} (UTC):");
                }
                msg.Append("`");
                if (ModProvider.IsMod(ctx.Message.Author.Id))
                    msg.Append($"[{evt.Id:0000}] ");
                msg.Append($"{evt.Start.AsUtc():u}");
                if (ctx.Channel.IsPrivate)
                    msg.Append($@" - {evt.End.AsUtc():u}");
                msg.AppendLine($@" ({evt.End.AsUtc() - evt.Start.AsUtc():h\:mm})`: {evt.Name}");
            }
            await ctx.SendAutosplitMessageAsync(msg, blockStart: "", blockEnd: "").ConfigureAwait(false);
        }

        private async Task<(bool success, DiscordMessage message)> EditEventPropertiesAsync(CommandContext ctx, EventSchedule evt, string eventName = null)
        {
            var interact = ctx.Client.GetInteractivity();
            var abort = DiscordEmoji.FromUnicode("🛑");
            var lastPage = DiscordEmoji.FromUnicode("↪");
            var firstPage = DiscordEmoji.FromUnicode("↩");
            var previousPage = DiscordEmoji.FromUnicode("⏪");
            var nextPage = DiscordEmoji.FromUnicode("⏩");
            var trash = DiscordEmoji.FromUnicode("🗑");
            var saveEdit = DiscordEmoji.FromUnicode("💾");

            var skipEventNameStep = !string.IsNullOrEmpty(eventName);
            DiscordMessage msg = null;
            string errorMsg = null;
            MessageContext txt;
            ReactionContext emoji;

        step1:
            // step 1: get the new start date
            var embed = FormatEvent(evt, errorMsg, 1).WithDescription($"Example: `{DateTime.UtcNow:yyyy-MM-dd HH:mm} [PST]`\nBy default all times use UTC, only limited number of time zones supported");
            msg = await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Please specify a new **start date and time**", embed: embed).ConfigureAwait(false);
            errorMsg = null;
            (msg, txt, emoji) = await interact.WaitForMessageOrReactionAsync(msg, ctx.User, abort, lastPage, nextPage, (evt.IsComplete() ? saveEdit : null)).ConfigureAwait(false);
            if (emoji != null)
            {
                if (emoji.Emoji == abort)
                    return (false, msg);

                if (emoji.Emoji == saveEdit)
                    return (true, msg);

                if (emoji.Emoji == lastPage)
                    goto step4;
            }
            else if (txt != null)
            {
                var newStartTime = FixTimeString(txt.Message.Content);
                if (!DateTime.TryParse(newStartTime, out var newTime))
                {
                    errorMsg = $"Couldn't parse `{newStartTime}` as a start date and time";
                    goto step1;
                }

                var duration = evt.End - evt.Start;
                newTime = Normalize(newTime);
                evt.Start = newTime.Ticks;
                evt.End = evt.Start + duration;
                evt.Year = newTime.Year;
            }
            else
                return (false, msg);

        step2:
            // step 2: get the new duration
            embed = FormatEvent(evt, errorMsg, 2).WithDescription("Example: `2d 1h 15m`, or `2.1:00`");
            msg = await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Please specify a new **event duration**", embed: embed.Build()).ConfigureAwait(false);
            errorMsg = null;
            (msg, txt, emoji) = await interact.WaitForMessageOrReactionAsync(msg, ctx.User, abort, previousPage, nextPage, (evt.IsComplete() ? saveEdit : null)).ConfigureAwait(false);
            if (emoji != null)
            {
                if (emoji.Emoji == abort)
                    return (false, msg);

                if (emoji.Emoji == saveEdit)
                    return (true, msg);

                if (emoji.Emoji == previousPage)
                    goto step1;

                if (skipEventNameStep)
                    goto step4;
            }
            else if (txt != null)
            {
                var newLength = await TryParseTimeSpanAsync(ctx, txt.Message.Content, false).ConfigureAwait(false);
                if (!newLength.HasValue)
                {
                    errorMsg = $"Couldn't parse `{txt.Message.Content}` as a duration";
                    goto step2;
                }

                evt.End = (evt.Start.AsUtc() + newLength.Value).Ticks;
            }
            else
                return (false, msg);

        step3:
            // step 3: get the new event name
            embed = FormatEvent(evt, errorMsg, 3);
            msg = await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Please specify a new **event name**", embed: embed.Build()).ConfigureAwait(false);
            errorMsg = null;
            (msg, txt, emoji) = await interact.WaitForMessageOrReactionAsync(msg, ctx.User, abort, previousPage, (string.IsNullOrEmpty(evt.EventName) ? null : trash), nextPage, (evt.IsComplete() ? saveEdit : null)).ConfigureAwait(false);
            if (emoji != null)
            {
                if (emoji.Emoji == abort)
                    return (false, msg);

                if (emoji.Emoji == saveEdit)
                    return (true, msg);

                if (emoji.Emoji == previousPage)
                    goto step2;

                if (emoji.Emoji == trash)
                    evt.EventName = null;
            }
            else if (txt != null)
                evt.EventName = string.IsNullOrWhiteSpace(txt.Message.Content) || txt.Message.Content == "-" ? null : txt.Message.Content;
            else
                return (false, msg);

        step4:
            // step 4: get the new schedule entry name
            embed = FormatEvent(evt, errorMsg, 4);
            msg = await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Please specify a new **schedule entry title**", embed: embed.Build()).ConfigureAwait(false);
            errorMsg = null;
            (msg, txt, emoji) = await interact.WaitForMessageOrReactionAsync(msg, ctx.User, abort, previousPage, firstPage, (evt.IsComplete() ? saveEdit : null)).ConfigureAwait(false);
            if (emoji != null)
            {
                if (emoji.Emoji == abort)
                    return (false, msg);

                if (emoji.Emoji == saveEdit)
                    return (true, msg);

                if (emoji.Emoji == firstPage)
                    goto step1;

                if (emoji.Emoji == previousPage)
                {
                    if (skipEventNameStep)
                        goto step2;
                    goto step3;
                }
            }
            else if (txt != null)
            {
                if (string.IsNullOrEmpty(txt.Message.Content))
                {
                    errorMsg = "Entry title cannot be empty";
                    goto step4;
                }

                evt.Name = txt.Message.Content;
            }
            else
                return (false, msg);

        step5:
            // step 5: confirm
            if (errorMsg == null && !evt.IsComplete())
                errorMsg = "Some required properties are not defined";
            embed = FormatEvent(evt, errorMsg);
            msg = await msg.UpdateOrCreateMessageAsync(ctx.Channel, "Does this look good? (y/n)", embed: embed.Build()).ConfigureAwait(false);
            errorMsg = null;
            (msg, txt, emoji) = await interact.WaitForMessageOrReactionAsync(msg, ctx.User, abort, previousPage, firstPage, (evt.IsComplete() ? saveEdit : null)).ConfigureAwait(false);
            if (emoji != null)
            {
                if (emoji.Emoji == abort)
                    return (false, msg);

                if (emoji.Emoji == saveEdit)
                    return (true, msg);

                if (emoji.Emoji == previousPage)
                    goto step4;

                if (emoji.Emoji == firstPage)
                    goto step1;
            }
            else if (!string.IsNullOrEmpty(txt?.Message.Content))
            {
                if (!evt.IsComplete())
                    goto step5;

                switch (txt.Message.Content.ToLowerInvariant())
                {
                    case "yes":
                    case "y":
                    case "✅":
                    case "☑":
                    case "✔":
                    case "👌":
                    case "👍":
                        return (true, msg);
                    case "no":
                    case "n":
                    case "❎":
                    case "❌":
                    case "👎":
                        return (false, msg);
                    default:
                        errorMsg = "I don't know what you mean, so I'll just abort";
                        goto step5;
                }
            }
            else
            {
                return (false, msg);
            }

            return (false, msg);
        }

        private static async Task<string> FuzzyMatchEventName(BotDb db, string eventName)
        {
            var knownEventNames = await db.EventSchedule.Select(e => e.EventName).Distinct().ToListAsync().ConfigureAwait(false);
            var (score, name) = knownEventNames.Select(n => (score: eventName.GetFuzzyCoefficientCached(n), name: n)).OrderByDescending(t => t.score).FirstOrDefault();
            return score > 0.8 ? name : eventName;
        }

        private static string FixTimeString(string dateTime)
        {
            return dateTime.ToUpperInvariant()
                .Replace("PST", "-08:00")
                .Replace("EST", "-05:00")
                .Replace("BST", "-03:00")
                .Replace("AEST", "+10:00");
        }

        private static DateTime Normalize(DateTime date)
        {
            if (date.Kind == DateTimeKind.Utc)
                return date;
            if (date.Kind == DateTimeKind.Local)
                return date.ToUniversalTime();
            return date.AsUtc();
        }

        private static async Task<TimeSpan?> TryParseTimeSpanAsync(CommandContext ctx, string duration, bool react = true)
        {
            var d = Duration.Match(duration);
            if (!d.Success)
            {
                if (react)
                    await ctx.ReactWithAsync(Config.Reactions.Failure, $"Failed to parse `{duration}` as a time", true).ConfigureAwait(false);
                return null;
            }

            int.TryParse(d.Groups["days"].Value, out var days);
            int.TryParse(d.Groups["hours"].Value, out var hours);
            int.TryParse(d.Groups["mins"].Value, out var mins);
            if (days == 0 && hours == 0 && mins == 0)
            {
                if (react)
                    await ctx.ReactWithAsync(Config.Reactions.Failure, $"Failed to parse `{duration}` as a time", true).ConfigureAwait(false);
                return null;
            }

            return new TimeSpan(days, hours, mins, 0);
        }

        private static string FormatCountdown(TimeSpan timeSpan)
        {
            var result = "";
            var days = (int)timeSpan.TotalDays;
            if (days > 0)
                timeSpan -= TimeSpan.FromDays(days);
            var hours = (int) timeSpan.TotalHours;
            if (hours > 0)
                timeSpan -= TimeSpan.FromHours(hours);
            var mins = (int) timeSpan.TotalMinutes;
            if (mins > 0)
                timeSpan -= TimeSpan.FromMinutes(mins);
            var secs = (int) timeSpan.TotalSeconds;
            if (days > 0)
                result += $"{days} day{(days == 1 ? "" : "s")} ";
            if (hours > 0 || days > 0)
                result += $"{hours} hour{(hours == 1 ? "" : "s")} ";
            if (mins > 0 || hours > 0 || days > 0)
                result += $"{mins} minute{(mins == 1 ? "" : "s")} ";
            result += $"{secs} second{(secs == 1 ? "" : "s")}";
            return result;
        }

        private static DiscordEmbedBuilder FormatEvent(EventSchedule evt, string error = null, int highlight = -1)
        {
            var start = evt.Start.AsUtc();
            var field = 1;
            var result = new DiscordEmbedBuilder
                {
                    Title = "Schedule entry preview",
                    Color = string.IsNullOrEmpty(error) ? Config.Colors.Help : Config.Colors.Maintenance,
                };
            if (!string.IsNullOrEmpty(error))
                result.AddField("Entry error", error);
            var currentTime = DateTime.UtcNow;
            if (evt.Start > currentTime.Ticks)
                result.WithFooter($"Starts in {FormatCountdown(evt.Start.AsUtc() - currentTime)}");
            else if (evt.End > currentTime.Ticks)
                result.WithFooter($"Ends in {FormatCountdown(evt.End.AsUtc() - currentTime)}");
            return result
                .AddFieldEx("Start time", evt.Start == 0 ? "-" : start.ToString("u"), highlight == field++, true)
                .AddFieldEx("Duration", evt.Start == evt.End ? "-" : (evt.End.AsUtc() - start).ToString(@"d\d\ h\h\ m\m"), highlight == field++, true)
                .AddFieldEx("Event name", string.IsNullOrEmpty(evt.EventName) ? "-" : evt.EventName, highlight == field++, true)
                .AddFieldEx("Schedule entry title", string.IsNullOrEmpty(evt.Name) ? "-" : evt.Name, highlight == field++, true);
        }
    }
}