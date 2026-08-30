using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// === НАЛАШТУВАННЯ ===
DotNetEnv.Env.TraversePath().Load(); // Завантажує змінні з файлу .env

var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN") 
               ?? throw new InvalidOperationException("BOT_TOKEN environment variable is missing!");

var botClient = new TelegramBotClient(botToken);

const int maxVoiceLimit = 20;       // Ліміт голосових на 24 години
const int maxVideoNoteLimit = 10;   // Ліміт відео-кружечків на 24 години
const int maxDurationSeconds = 60;  // Максимальна тривалість (у секундах)

var mediaHistory = new ConcurrentDictionary<string, List<DateTime>>();
var syncLock = new object();

using var cts = new CancellationTokenSource();

// 1. Отримуємо дані бота до запуску
var me = await botClient.GetMe(cancellationToken: cts.Token);
var botUsername = me.Username ?? "";

// 2. Встановлюємо список команд у меню
await botClient.SetMyCommands(
    commands:
    [
        new BotCommand { Command = "status", Description = "Перевірити залишок лімітів за 24 години" }
    ],
    cancellationToken: cts.Token
);

// 3. Запускаємо прийом повідомлень
botClient.StartReceiving(
    HandleUpdateAsync,
    HandlePollingErrorAsync,
    new ReceiverOptions { AllowedUpdates = [UpdateType.Message] },
    cts.Token
);

Console.WriteLine($"Бот @{botUsername} успішно запущений!");

// === ВЕБ-СЕРВЕР ДЛЯ ХОСТИНГУ RENDER / ХЕЛСЧЕК ===
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var listener = new HttpListener();

try
{
    listener.Prefixes.Add($"http://*:{port}/");
    listener.Start();
    Console.WriteLine($"HTTP сервер запущений на порту {port}");

    while (!cts.Token.IsCancellationRequested)
    {
        var context = await listener.GetContextAsync();
        var response = context.Response;
        var responseString = "Bot is active";
        var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, cts.Token);
        response.OutputStream.Close();
    }
}
catch (Exception)
{
    // Очікуємо клавішу Enter при локальному запуску на комп'ютері
    Console.ReadLine();
}

cts.Cancel();

// Метод очищення та підрахунку активних повідомлень за останні 24 години
int GetActiveCount(string key)
{
    lock (syncLock)
    {
        if (!mediaHistory.TryGetValue(key, out var list))
            return 0;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        list.RemoveAll(t => t <= cutoff);
        return list.Count;
    }
}

// Метод фіксації нового повідомлення
void RecordMessage(string key)
{
    lock (syncLock)
    {
        var list = mediaHistory.GetOrAdd(key, _ => new List<DateTime>());
        list.Add(DateTime.UtcNow);
    }
}

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is not { } message)
        return;

    var userId = message.From?.Id ?? 0;
    var chatId = message.Chat.Id;
    var userName = message.From?.FirstName ?? "Користувач";

    // ==========================================
    // 1. ОБРОБКА ТЕКСТОВИХ КОМАНД (ПЕРЕД МЕДІА)
    // ==========================================
    if (!string.IsNullOrEmpty(message.Text))
    {
        var cleanText = message.Text.Trim();

        if (cleanText.StartsWith("/limit", StringComparison.OrdinalIgnoreCase))
        {
            var voiceKey = $"{chatId}_{userId}_voice";
            var videoKey = $"{chatId}_{userId}_video_note";

            var usedVoice = GetActiveCount(voiceKey);
            var usedVideo = GetActiveCount(videoKey);

            var remainingVoice = Math.Max(0, maxVoiceLimit - usedVoice);
            var remainingVideo = Math.Max(0, maxVideoNoteLimit - usedVideo);

            var statusText = $"📊 <b>Ліміти за останні 24 години для {userName}:</b>\n\n" +
                             $"🎤 Голосові: <b>{remainingVoice}</b> з {maxVoiceLimit} доступно\n" +
                             $"📹 Кружечки: <b>{remainingVideo}</b> з {maxVideoNoteLimit} доступно\n" +
                             $"⏱ Макс. тривалість: <b>{maxDurationSeconds} сек</b>\n\n" +
                             $"<i>(Ліміт оновлюється кожні 24 години)</i>";

            await bot.SendMessage(
                chatId: chatId,
                text: statusText,
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct
            );
            return;
        }

        // Якщо це просто звичайне текстове повідомлення — ігноруємо
        return;
    }

    // ==========================================
    // 2. ОБРОБКА ГОЛОСОВИХ ТА КРУЖЕЧКІВ
    // ==========================================
    var (mediaType, mediaNameUa, limit, duration) = message switch
    {
        { Voice: { } v } => ("voice", "голосових", maxVoiceLimit, v.Duration),
        { VideoNote: { } vn } => ("video_note", "відео-кружечків", maxVideoNoteLimit, vn.Duration),
        _ => (null, "", 0, 0)
    };

    if (mediaType == null)
        return;

    // Перевірка тривалості
    if (duration > maxDurationSeconds)
    {
        try
        {
            await bot.DeleteMessage(chatId, message.MessageId, cancellationToken: ct);
            Console.WriteLine($"[ВИДАЛЕНО] Задовге повідомлення від {userName}: {duration}с");
        }
        catch (ApiRequestException ex)
        {
            Console.WriteLine($"[ПОМИЛКА ВИДАЛЕННЯ]: {ex.Message}");
        }

        await bot.SendMessage(
            chatId: chatId,
            text: $"⏳ {userName}, повідомлення занадто довге ({duration} сек)! Максимальна тривалість: {maxDurationSeconds} сек. Видалено.",
            cancellationToken: ct
        );
        return;
    }

    // Перевірка 24-годинного ліміту
    var historyKey = $"{chatId}_{userId}_{mediaType}";
    var currentCount = GetActiveCount(historyKey);

    if (currentCount >= limit)
    {
        try
        {
            await bot.DeleteMessage(chatId, message.MessageId, cancellationToken: ct);
            Console.WriteLine($"[ВИДАЛЕНО] Вичерпано 24-годинний ліміт для {userName}");
        }
        catch (ApiRequestException ex)
        {
            Console.WriteLine($"[ПОМИЛКА ВИДАЛЕННЯ]: {ex.Message}");
        }

        await bot.SendMessage(
            chatId: chatId,
            text: $"⚠️ {userName}, ви вичерпали ліміт {mediaNameUa} на 24 години ({limit}/{limit}). Повідомлення видалено.",
            cancellationToken: ct
        );
    }
    else
    {
        RecordMessage(historyKey);
        var remaining = limit - (currentCount + 1);

        // Попередження тільки якщо залишилось 3 або 1
        if (remaining is 3 or 1)
        {
            await bot.SendMessage(
                chatId: chatId,
                text: $"⚠️ {userName}, увага! Залишилось лише <b>{remaining}</b> {mediaNameUa} на найближчі 24 години.",
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct
            );
        }
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
{
    var errorMessage = exception switch
    {
        ApiRequestException apiEx => $"Telegram API Error: [{apiEx.ErrorCode}] {apiEx.Message}",
        _ => exception.ToString()
    };

    Console.WriteLine(errorMessage);
    return Task.CompletedTask;
}