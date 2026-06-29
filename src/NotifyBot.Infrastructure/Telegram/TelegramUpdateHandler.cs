using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Domain.Entities;
using NotifyBot.Domain.Enums;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramUpdateHandler(
    ITelegramBotClient botClient,
    ITelegramChatRepository chatRepository,
    IAdminUserRepository adminUserRepository,
    ITelegramService telegramService,
    ISmsCheckService smsCheckService,
    TelegramAdminPanel adminPanel,
    IOptions<TelegramOptions> options,
    ILogger<TelegramUpdateHandler> logger) : ITelegramUpdateHandler
{
    private readonly TelegramOptions _options = options.Value;
    private string? _botUsername;

    public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
    {
        if (update.CallbackQuery is { } callback)
        {
            await adminPanel.HandleCallbackAsync(callback, cancellationToken);
            return;
        }

        if (update.MyChatMember is { } membership)
        {
            await HandleChatMembershipAsync(membership, cancellationToken);
            return;
        }

        if (update.Message is { } message)
        {
            await RegisterChatFromMessageAsync(message, cancellationToken);

            if (message.From is null)
            {
                return;
            }

            if (message.Text is not null && message.Text.StartsWith('/'))
            {
                await HandleCommandAsync(message, cancellationToken);
                return;
            }

            if (message.Text is not null && await IsBotMentionedAsync(message, cancellationToken))
            {
                await HandleSmsCheckAsync(message, cancellationToken);
                return;
            }

            if (message.Text is not null && message.Chat.Type == ChatType.Private)
            {
                await adminPanel.HandleTextInputAsync(message, cancellationToken);
            }
        }
    }

    private async Task HandleChatMembershipAsync(ChatMemberUpdated membership, CancellationToken cancellationToken)
    {
        if (membership.NewChatMember.User.IsBot != true)
        {
            return;
        }

        var chat = membership.Chat;

        if (membership.NewChatMember.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator)
        {
            var registered = await RegisterChatAsync(chat, cancellationToken);
            if (registered)
            {
                await telegramService.SendAdminMessageAsync(
                    $"Бот добавлен в чат:\n{TelegramAdminPanel.FormatChatLine(MapChat(chat))}\n\nОткройте бота в личке → раздел «Чаты».",
                    cancellationToken);
            }
        }
        else if (membership.NewChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked)
        {
            await chatRepository.DeactivateAsync(chat.Id, cancellationToken);
        }
    }

    private async Task RegisterChatFromMessageAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Chat is null)
        {
            return;
        }

        await RegisterChatAsync(message.Chat, cancellationToken);
    }

    private async Task<bool> RegisterChatAsync(Chat chat, CancellationToken cancellationToken)
    {
        var mapped = MapChat(chat);
        var existing = await chatRepository.GetByChatIdAsync(chat.Id, cancellationToken);
        var isNew = existing is null;

        mapped.LastSeenAtUtc = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            mapped.IsActive = true;
        }

        await chatRepository.UpsertAsync(mapped, cancellationToken);
        logger.LogInformation("Telegram chat registered: {ChatId} ({Type})", chat.Id, mapped.ChatType);
        return isNew;
    }

    private async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var user = message.From!;
        var command = message.Text!.Trim().Split(' ')[0].Split('@')[0].ToLowerInvariant();

        if (command is "/sms" or "/check")
        {
            await HandleSmsCheckAsync(message, cancellationToken);
            return;
        }

        if (command is "/sms_all")
        {
            if (!_options.IsAdminUsername(user.Username))
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "Команда только для администраторов.",
                    cancellationToken: cancellationToken);
                return;
            }

            await HandleSmsAllDebugAsync(message, cancellationToken);
            return;
        }

        if (command is not ("/start" or "/menu" or "/help"))
        {
            if (message.Chat.Type == ChatType.Private && _options.IsAdminUsername(user.Username))
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "Используйте кнопки меню. Откройте /start",
                    cancellationToken: cancellationToken);
            }

            return;
        }

        if (!_options.IsAdminUsername(user.Username))
        {
            if (message.Chat.Type == ChatType.Private)
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "Доступ только для администраторов.",
                    cancellationToken: cancellationToken);
            }

            return;
        }

        if (message.Chat.Type != ChatType.Private)
        {
            await botClient.SendMessage(
                message.Chat.Id,
                "Панель управления доступна в личных сообщениях с ботом.",
                cancellationToken: cancellationToken);
            return;
        }

        await adminUserRepository.UpsertAsync(new AdminUser
        {
            TelegramUserId = user.Id,
            Username = user.Username,
            IsActive = true
        }, cancellationToken);

        await adminPanel.ShowMainMenuAsync(message.Chat.Id, cancellationToken: cancellationToken);
    }

    private async Task HandleSmsCheckAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        try
        {
            var reply = await smsCheckService.CheckAsync(chatId, cancellationToken);
            await botClient.SendMessage(chatId, reply, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle SMS check for chat {ChatId}", chatId);
            await botClient.SendMessage(
                chatId,
                "Ошибка при проверке SMS. Попробуйте снова.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleSmsAllDebugAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        try
        {
            var reply = await smsCheckService.ListAllForDebugAsync(cancellationToken);
            await botClient.SendMessage(chatId, reply, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle SMS debug list for chat {ChatId}", chatId);
            await botClient.SendMessage(
                chatId,
                "Ошибка при получении списка SMS.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> IsBotMentionedAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Entities is null || message.Text is null)
        {
            return false;
        }

        var botUsername = await GetBotUsernameAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(botUsername))
        {
            return false;
        }

        var mention = $"@{botUsername}";
        return message.Entities.Any(entity =>
            entity.Type == MessageEntityType.Mention &&
            entity.Offset >= 0 &&
            entity.Length > 0 &&
            entity.Offset + entity.Length <= message.Text.Length &&
            message.Text.Substring(entity.Offset, entity.Length)
                .Equals(mention, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> GetBotUsernameAsync(CancellationToken cancellationToken)
    {
        if (_botUsername is not null)
        {
            return _botUsername;
        }

        var me = await botClient.GetMe(cancellationToken);
        _botUsername = me.Username;
        return _botUsername;
    }

    private static TelegramChat MapChat(Chat chat) =>
        new()
        {
            ChatId = chat.Id,
            ChatType = chat.Type switch
            {
                ChatType.Private => TelegramChatType.Private,
                ChatType.Group => TelegramChatType.Group,
                ChatType.Supergroup => TelegramChatType.Supergroup,
                ChatType.Channel => TelegramChatType.Channel,
                _ => TelegramChatType.Group
            },
            Title = chat.Title ?? chat.FirstName,
            Username = chat.Username,
            IsActive = true,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };
}