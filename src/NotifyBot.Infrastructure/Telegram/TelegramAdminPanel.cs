using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramAdminPanel(
    ITelegramBotClient botClient,
    ITelegramChatRepository chatRepository,
    ICardRepository cardRepository,
    ISmsCheckService smsCheckService,
    AdminSessionStore sessionStore,
    IOptions<TelegramOptions> options,
    ILogger<TelegramAdminPanel> logger)
{
    private const int ChatsPerPage = 6;
    private readonly TelegramOptions _options = options.Value;

    public async Task ShowMainMenuAsync(long chatId, int? messageId = null, CancellationToken cancellationToken = default)
    {
        sessionStore.Clear(chatId);
        const string text =
            """
            NotifyBot — панель управления

            Здесь настраивается, куда уходят 3DS-коды с карт.
            Выберите раздел кнопкой ниже.
            """;

        var keyboard = new InlineKeyboardMarkup([
            [Btn("💳 Карты", "m|cards"), Btn("💬 Чаты", "m|chats")],
            [Btn("📋 Все SMS", "m|smsdebug")],
            [Btn("ℹ️ Как это работает", "m|help")]
        ]);

        await SendOrEditAsync(chatId, messageId, text, keyboard, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken cancellationToken = default)
    {
        var user = query.From;
        if (!_options.IsAdminUsername(user.Username))
        {
            await botClient.AnswerCallbackQuery(query.Id, "Доступ только для администраторов.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var chatId = query.Message?.Chat.Id ?? user.Id;
        var messageId = query.Message?.MessageId;
        var data = query.Data ?? string.Empty;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: cancellationToken);

        try
        {
            await DispatchCallbackAsync(user.Id, chatId, messageId, data, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle admin callback {Data}", data);
            await botClient.SendMessage(chatId, "Ошибка. Попробуйте снова или откройте /start.", cancellationToken: cancellationToken);
        }
    }

    public async Task HandleTextInputAsync(Message message, CancellationToken cancellationToken = default)
    {
        var user = message.From;
        if (user is null || message.Text is null || !_options.IsAdminUsername(user.Username))
        {
            return;
        }

        if (!sessionStore.TryGet(user.Id, out var state))
        {
            return;
        }

        var text = message.Text.Trim();
        var chatId = message.Chat.Id;

        switch (state.Kind)
        {
            case AdminInputKind.AddCard:
                await HandleAddCardInputAsync(message, text, cancellationToken);
                break;
            case AdminInputKind.EditCardLabel when state.CardLast4 is not null:
                await HandleEditLabelInputAsync(chatId, state.CardLast4, text, cancellationToken);
                break;
        }

        sessionStore.Clear(user.Id);
    }

    private async Task DispatchCallbackAsync(long userId, long chatId, int? messageId, string data, CancellationToken cancellationToken)
    {
        var parts = data.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            await ShowMainMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        switch (parts[0])
        {
            case "m":
                await HandleMenuCallbackAsync(chatId, messageId, parts, cancellationToken);
                break;
            case "c":
                await HandleCardCallbackAsync(userId, chatId, messageId, parts, cancellationToken);
                break;
            case "ch":
                await HandleChatCallbackAsync(chatId, messageId, parts, cancellationToken);
                break;
            default:
                await ShowMainMenuAsync(chatId, messageId, cancellationToken);
                break;
        }
    }

    private async Task HandleMenuCallbackAsync(long chatId, int? messageId, string[] parts, CancellationToken cancellationToken)
    {
        var section = parts.Length > 1 ? parts[1] : "main";
        switch (section)
        {
            case "main":
                await ShowMainMenuAsync(chatId, messageId, cancellationToken);
                break;
            case "cards":
                await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
                break;
            case "chats":
                await ShowChatsMenuAsync(chatId, messageId, cancellationToken);
                break;
            case "help":
                await ShowHelpAsync(chatId, messageId, cancellationToken);
                break;
            case "smsdebug":
                await ShowSmsDebugAsync(chatId, cancellationToken);
                break;
            default:
                await ShowMainMenuAsync(chatId, messageId, cancellationToken);
                break;
        }
    }

    private async Task ShowSmsDebugAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await smsCheckService.ListAllForDebugAsync(cancellationToken);
            await botClient.SendMessage(chatId, reply, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SMS debug list for admin chat {ChatId}", chatId);
            await botClient.SendMessage(
                chatId,
                "Не удалось получить список SMS из Plusofon.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCardCallbackAsync(long userId, long chatId, int? messageId, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        if (parts[1] == "add")
        {
            sessionStore.Set(userId, new AdminInputState(AdminInputKind.AddCard));
            await botClient.SendMessage(
                chatId,
                "Введите последние 4 цифры карты.\n\nМожно сразу с названием:\n`1062 Office3`",
                parseMode: ParseMode.Markdown,
                replyMarkup: CancelKeyboard("m|cards"),
                cancellationToken: cancellationToken);
            return;
        }

        var last4 = parts[1];
        if (!IsValidLast4(last4))
        {
            await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        if (parts.Length == 2)
        {
            await ShowCardDetailAsync(chatId, messageId, last4, cancellationToken);
            return;
        }

        switch (parts[2])
        {
            case "bind":
                var page = parts.Length > 4 && parts[3] == "p" && int.TryParse(parts[4], out var p) ? p : 0;
                await ShowBindChatPickerAsync(chatId, messageId, last4, page, cancellationToken);
                break;
            case "bindto" when parts.Length > 3 && long.TryParse(parts[3], out var destChatId):
                await BindCardAsync(chatId, messageId, last4, destChatId, cancellationToken);
                break;
            case "unbind":
                await cardRepository.SetDestinationAsync(last4, null, cancellationToken);
                await ShowCardDetailAsync(chatId, messageId, last4, cancellationToken);
                break;
            case "toggle":
                await ToggleCardAsync(last4, cancellationToken);
                await ShowCardDetailAsync(chatId, messageId, last4, cancellationToken);
                break;
            case "edit":
                sessionStore.Set(userId, new AdminInputState(AdminInputKind.EditCardLabel, last4));
                await botClient.SendMessage(
                    chatId,
                    $"Введите новое название для карты *{last4}*.\n\nОтправьте `-` чтобы очистить.",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: CancelKeyboard($"c|{last4}"),
                    cancellationToken: cancellationToken);
                break;
            case "del":
                await ShowCardDeleteConfirmAsync(chatId, messageId, last4, cancellationToken);
                break;
            case "delok":
                await cardRepository.DeleteAsync(last4, cancellationToken);
                await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
                break;
            default:
                await ShowCardDetailAsync(chatId, messageId, last4, cancellationToken);
                break;
        }
    }

    private async Task HandleChatCallbackAsync(long chatId, int? messageId, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2 || !long.TryParse(parts[1], out var targetChatId))
        {
            await ShowChatsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        if (parts.Length == 2)
        {
            await ShowChatDetailAsync(chatId, messageId, targetChatId, cancellationToken);
            return;
        }

        switch (parts[2])
        {
            case "del":
                await ShowChatDeleteConfirmAsync(chatId, messageId, targetChatId, cancellationToken);
                break;
            case "delok":
                await DeactivateChatAsync(targetChatId, cancellationToken);
                await ShowChatsMenuAsync(chatId, messageId, cancellationToken);
                break;
            default:
                await ShowChatDetailAsync(chatId, messageId, targetChatId, cancellationToken);
                break;
        }
    }

    private async Task ShowCardsMenuAsync(long chatId, int? messageId, CancellationToken cancellationToken)
    {
        var cards = await cardRepository.GetAllAsync(cancellationToken);
        var chats = (await chatRepository.GetActiveChatsAsync(cancellationToken)).ToDictionary(x => x.ChatId);

        var lines = cards.Count == 0
            ? ["Карт пока нет. Нажмите «Добавить карту»."]
            : cards.Select(c => FormatCardSummary(c, chats)).ToArray();

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var card in cards)
        {
            var icon = card.Enabled ? "✅" : "⛔";
            var label = string.IsNullOrWhiteSpace(card.Label) ? card.Last4 : $"{card.Last4} · {card.Label}";
            rows.Add([Btn($"{icon} {label}", $"c|{card.Last4}")]);
        }

        rows.Add([Btn("➕ Добавить карту", "c|add")]);
        rows.Add([Btn("⬅️ Главное меню", "m|main")]);

        await SendOrEditAsync(
            chatId,
            messageId,
            "💳 Карты\n\n" + string.Join("\n", lines),
            new InlineKeyboardMarkup(rows),
            cancellationToken);
    }

    private async Task ShowCardDetailAsync(long chatId, int? messageId, string last4, CancellationToken cancellationToken)
    {
        var card = await cardRepository.GetByLast4Async(last4, cancellationToken);
        if (card is null)
        {
            await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        var chats = (await chatRepository.GetActiveChatsAsync(cancellationToken)).ToDictionary(x => x.ChatId);
        var destination = FormatDestination(card, chats);
        var label = string.IsNullOrWhiteSpace(card.Label) ? "—" : card.Label;
        var status = card.Enabled ? "включена ✅" : "отключена ⛔";

        var text =
            $"""
             💳 Карта *{card.Last4}*

             Название: {label}
             Статус: {status}
             Получатель: {destination}
             """;

        var keyboardRows = new List<InlineKeyboardButton[]>
        {
            new[] { Btn("🔗 Привязать к чату", $"c|{last4}|bind"), Btn("✏️ Название", $"c|{last4}|edit") },
            new[] { Btn(card.Enabled ? "⛔ Отключить" : "✅ Включить", $"c|{last4}|toggle") }
        };

        if (card.DestinationChatId is not null)
        {
            keyboardRows.Add([Btn("❌ Отвязать чат", $"c|{last4}|unbind")]);
        }

        keyboardRows.Add([Btn("🗑 Удалить карту", $"c|{last4}|del")]);
        keyboardRows.Add([Btn("⬅️ К списку карт", "m|cards")]);
        var keyboard = new InlineKeyboardMarkup(keyboardRows);

        await SendOrEditAsync(chatId, messageId, text, keyboard, cancellationToken, ParseMode.Markdown);
    }

    private async Task ShowBindChatPickerAsync(long chatId, int? messageId, string last4, int page, CancellationToken cancellationToken)
    {
        var chats = await chatRepository.GetActiveChatsAsync(cancellationToken);
        if (chats.Count == 0)
        {
            await SendOrEditAsync(
                chatId,
                messageId,
                "💬 Чатов нет.\n\nДобавьте бота в группу или напишите ему в личку, затем обновите список.",
                new InlineKeyboardMarkup([
                    [Btn("🔄 Обновить", $"c|{last4}|bind")],
                    [Btn("⬅️ Назад", $"c|{last4}")]
                ]),
                cancellationToken);
            return;
        }

        var totalPages = (int)Math.Ceiling(chats.Count / (double)ChatsPerPage);
        page = Math.Clamp(page, 0, Math.Max(0, totalPages - 1));
        var pageChats = chats.Skip(page * ChatsPerPage).Take(ChatsPerPage).ToList();

        var rows = pageChats
            .Select(ch => new[] { Btn(FormatChatButton(ch), $"c|{last4}|bindto|{ch.ChatId}") })
            .ToList();

        var nav = new List<InlineKeyboardButton>();
        if (page > 0)
        {
            nav.Add(Btn("◀️", $"c|{last4}|bind|p|{page - 1}"));
        }

        nav.Add(Btn($"Стр. {page + 1}/{totalPages}", $"c|{last4}|bind|p|{page}"));

        if (page < totalPages - 1)
        {
            nav.Add(Btn("▶️", $"c|{last4}|bind|p|{page + 1}"));
        }

        rows.Add(nav.ToArray());
        rows.Add([Btn("⬅️ Назад", $"c|{last4}")]);

        await SendOrEditAsync(
            chatId,
            messageId,
            $"🔗 Куда отправлять коды карты *{last4}*?\n\nВыберите чат:",
            new InlineKeyboardMarkup(rows),
            cancellationToken,
            ParseMode.Markdown);
    }

    private async Task ShowChatsMenuAsync(long chatId, int? messageId, CancellationToken cancellationToken)
    {
        var chats = await chatRepository.GetActiveChatsAsync(cancellationToken);
        var lines = chats.Count == 0
            ? ["Чатов пока нет.", "", "Как добавить:", "• добавьте бота в группу", "• или напишите боту в личные сообщения"]
            : chats.Select(FormatChatLine).ToArray();

        var rows = chats
            .Select(ch => new[] { Btn(FormatChatButton(ch), $"ch|{ch.ChatId}") })
            .ToList();
        rows.Add([Btn("⬅️ Главное меню", "m|main")]);

        await SendOrEditAsync(
            chatId,
            messageId,
            "💬 Чаты\n\n" + string.Join("\n", lines),
            new InlineKeyboardMarkup(rows),
            cancellationToken);
    }

    private async Task ShowChatDetailAsync(long chatId, int? messageId, long targetChatId, CancellationToken cancellationToken)
    {
        var chat = await chatRepository.GetByChatIdAsync(targetChatId, cancellationToken);
        if (chat is null || !chat.IsActive)
        {
            await ShowChatsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        var cards = await cardRepository.GetAllAsync(cancellationToken);
        var bound = cards.Where(c => c.DestinationChatId == targetChatId).Select(c => c.Last4).ToList();
        var boundText = bound.Count == 0 ? "нет привязанных карт" : string.Join(", ", bound.Select(x => $"*{x}*"));

        var text =
            $"""
             💬 {FormatChatLine(chat)}

             Привязанные карты: {boundText}
             """;

        var keyboard = new InlineKeyboardMarkup([
            [Btn("🗑 Удалить из списка", $"ch|{targetChatId}|del")],
            [Btn("⬅️ К списку чатов", "m|chats")]
        ]);

        await SendOrEditAsync(chatId, messageId, text, keyboard, cancellationToken, ParseMode.Markdown);
    }

    private async Task ShowHelpAsync(long chatId, int? messageId, CancellationToken cancellationToken)
    {
        const string text =
            """
            ℹ️ Как настроить NotifyBot

            1. Добавьте бота в группу офиса или используйте личные сообщения.
            2. В разделе «Чаты» убедитесь, что чат появился.
            3. В разделе «Карты» добавьте карту (4 цифры).
            4. Откройте карту → «Привязать к чату».

            После оплаты: /sms или /check в чате офиса (можно тегнуть бота).
            Если кода ещё нет — бот сам подождёт и пришлёт, когда SMS появится.
            Админам: кнопка «Все SMS» в главном меню — сырой список из Plusofon.
            """;

        await SendOrEditAsync(
            chatId,
            messageId,
            text,
            new InlineKeyboardMarkup([[Btn("⬅️ Главное меню", "m|main")]]),
            cancellationToken);
    }

    private async Task ShowCardDeleteConfirmAsync(long chatId, int? messageId, string last4, CancellationToken cancellationToken)
    {
        await SendOrEditAsync(
            chatId,
            messageId,
            $"Удалить карту *{last4}*?\n\nЭто действие нельзя отменить.",
            new InlineKeyboardMarkup([
                [Btn("🗑 Да, удалить", $"c|{last4}|delok")],
                [Btn("⬅️ Отмена", $"c|{last4}")]
            ]),
            cancellationToken,
            ParseMode.Markdown);
    }

    private async Task ShowChatDeleteConfirmAsync(long chatId, int? messageId, long targetChatId, CancellationToken cancellationToken)
    {
        var chat = await chatRepository.GetByChatIdAsync(targetChatId, cancellationToken);
        var title = chat is null ? targetChatId.ToString() : FormatChatButton(chat);

        await SendOrEditAsync(
            chatId,
            messageId,
            $"Убрать чат из списка?\n\n{title}\n\nПривязки карт к этому чату будут сняты.",
            new InlineKeyboardMarkup([
                [Btn("🗑 Да, убрать", $"ch|{targetChatId}|delok")],
                [Btn("⬅️ Отмена", $"ch|{targetChatId}")]
            ]),
            cancellationToken);
    }

    private async Task HandleAddCardInputAsync(Message message, string text, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !IsValidLast4(parts[0]))
        {
            sessionStore.Set(message.From!.Id, new AdminInputState(AdminInputKind.AddCard));
            await botClient.SendMessage(chatId, "Нужны 4 цифры. Пример: `1062` или `1062 Office3`", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            return;
        }

        var last4 = parts[0];
        var label = parts.Length > 1 ? parts[1] : null;
        var existing = await cardRepository.GetByLast4Async(last4, cancellationToken);

        await cardRepository.UpsertAsync(new Card
        {
            Last4 = last4,
            Label = label,
            Enabled = existing?.Enabled ?? true,
            DestinationChatId = existing?.DestinationChatId
        }, cancellationToken);

        await ShowCardDetailAsync(message.Chat.Id, null, last4, cancellationToken);
    }

    private async Task HandleEditLabelInputAsync(long chatId, string last4, string text, CancellationToken cancellationToken)
    {
        var card = await cardRepository.GetByLast4Async(last4, cancellationToken);
        if (card is null)
        {
            await ShowCardsMenuAsync(chatId, null, cancellationToken);
            return;
        }

        card.Label = text == "-" ? null : text.Trim();
        if (string.IsNullOrWhiteSpace(card.Label))
        {
            card.Label = null;
        }

        await cardRepository.UpsertAsync(card, cancellationToken);
        await ShowCardDetailAsync(chatId, null, last4, cancellationToken);
    }

    private async Task BindCardAsync(long chatId, int? messageId, string last4, long destChatId, CancellationToken cancellationToken)
    {
        var card = await cardRepository.GetByLast4Async(last4, cancellationToken);
        if (card is null)
        {
            await ShowCardsMenuAsync(chatId, messageId, cancellationToken);
            return;
        }

        await cardRepository.SetDestinationAsync(last4, destChatId, cancellationToken);
        await ShowCardDetailAsync(chatId, messageId, last4, cancellationToken);
    }

    private async Task ToggleCardAsync(string last4, CancellationToken cancellationToken)
    {
        var card = await cardRepository.GetByLast4Async(last4, cancellationToken);
        if (card is null)
        {
            return;
        }

        card.Enabled = !card.Enabled;
        await cardRepository.UpsertAsync(card, cancellationToken);
    }

    private async Task DeactivateChatAsync(long targetChatId, CancellationToken cancellationToken)
    {
        await chatRepository.DeactivateAsync(targetChatId, cancellationToken);

        var cards = await cardRepository.GetAllAsync(cancellationToken);
        foreach (var card in cards.Where(c => c.DestinationChatId == targetChatId))
        {
            await cardRepository.SetDestinationAsync(card.Last4, null, cancellationToken);
        }
    }

    private async Task SendOrEditAsync(
        long chatId,
        int? messageId,
        string text,
        InlineKeyboardMarkup keyboard,
        CancellationToken cancellationToken,
        ParseMode parseMode = ParseMode.None)
    {
        if (messageId is not null)
        {
            try
            {
                await botClient.EditMessageText(chatId, messageId.Value, text, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "EditMessage failed, sending new message");
            }
        }

        await botClient.SendMessage(chatId, text, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: cancellationToken);
    }

    private static InlineKeyboardButton Btn(string label, string callbackData) =>
        InlineKeyboardButton.WithCallbackData(label, callbackData);

    private static InlineKeyboardMarkup CancelKeyboard(string backCallback) =>
        new([[Btn("❌ Отмена", backCallback)]]);

    private static bool IsValidLast4(string value) =>
        value.Length == 4 && value.All(char.IsDigit);

    private static string FormatCardSummary(Card card, IReadOnlyDictionary<long, TelegramChat> chats)
    {
        var destination = FormatDestination(card, chats);
        var status = card.Enabled ? "✅" : "⛔";
        var label = string.IsNullOrWhiteSpace(card.Label) ? "" : $" ({card.Label})";
        return $"{status} *{card.Last4}*{label} → {destination}";
    }

    private static string FormatDestination(Card card, IReadOnlyDictionary<long, TelegramChat> chats) =>
        card.DestinationChatId is null
            ? "не привязана"
            : chats.TryGetValue(card.DestinationChatId.Value, out var chat)
                ? FormatChatButton(chat)
                : $"id {card.DestinationChatId}";

    private static string FormatChatButton(TelegramChat chat)
    {
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "Без названия" : chat.Title;
        if (title.Length > 28)
        {
            title = title[..25] + "...";
        }

        var type = chat.ChatType switch
        {
            Domain.Enums.TelegramChatType.Private => "ЛС",
            Domain.Enums.TelegramChatType.Group => "группа",
            Domain.Enums.TelegramChatType.Supergroup => "супергруппа",
            Domain.Enums.TelegramChatType.Channel => "канал",
            _ => "чат"
        };

        return $"{title} ({type})";
    }

    public static string FormatChatLine(TelegramChat chat)
    {
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "Без названия" : chat.Title;
        var username = string.IsNullOrWhiteSpace(chat.Username) ? "" : $" @{chat.Username}";
        var type = chat.ChatType switch
        {
            Domain.Enums.TelegramChatType.Private => "личный",
            Domain.Enums.TelegramChatType.Group => "группа",
            Domain.Enums.TelegramChatType.Supergroup => "супергруппа",
            Domain.Enums.TelegramChatType.Channel => "канал",
            _ => chat.ChatType.ToString()
        };

        return $"• {title}{username} — {type}";
    }
}